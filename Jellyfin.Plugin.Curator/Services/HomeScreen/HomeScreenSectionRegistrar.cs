using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.Curator.Core.HomeScreen;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Curator.Services.HomeScreen
{
    /// <summary>
    /// Hands section registrations to Home Screen Sections by reflection.
    ///
    /// <para>
    /// Reflection rather than a project reference, for two reasons. Home Screen
    /// Sections is not on NuGet, so referencing it would mean carrying a copy of
    /// somebody else's DLL; and a plugin assembly that fails to resolve a reference
    /// does not fail gracefully in Jellyfin, it fails to load at all — Curator would
    /// stop existing on any server that had not installed the other plugin, rather
    /// than logging that rows are unavailable and carrying on building playlists.
    /// Collection Sections reaches the same method the same way, which is the
    /// working example this follows.
    /// </para>
    ///
    /// <para>
    /// Nothing here is on a hot path: registration happens once at startup and once
    /// per sync, so the cost of reflecting is irrelevant beside the cost of a hard
    /// dependency.
    /// </para>
    /// </summary>
    public class HomeScreenSectionRegistrar : ISectionRegistrar
    {
        /// <summary>The type exposing the other plugin's registration entry point.</summary>
        public const string PluginInterfaceTypeName = "Jellyfin.Plugin.HomeScreenSections.PluginInterface";

        /// <summary>The registration method on that type.</summary>
        public const string RegisterMethodName = "RegisterSection";

        private const string AssemblyFragment = ".HomeScreenSections";

        private readonly ILogger<HomeScreenSectionRegistrar> _logger;

        public HomeScreenSectionRegistrar(ILogger<HomeScreenSectionRegistrar> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public int? RegisterSections(IReadOnlyList<SectionRegistrationRequest> sections)
        {
            ArgumentNullException.ThrowIfNull(sections);

            var entryPoint = ResolveEntryPoint();
            if (entryPoint is null)
            {
                return null;
            }

            var (register, payloadFactory) = entryPoint.Value;

            var registered = 0;
            foreach (var request in sections)
            {
                var section = request.Section;
                var assemblyName = request.ResultsType.Assembly.FullName;
                var className = request.ResultsType.FullName;
                if (assemblyName is null || className is null)
                {
                    _logger.LogWarning(
                        "Curator: could not name the results handler for '{Name}'; that row cannot be registered",
                        section.Name);
                    continue;
                }

                var json = SectionRegistration.BuildPayload(
                    section, request.AdditionalData, assemblyName, className);

                try
                {
                    register.Invoke(null, [payloadFactory(json)]);
                    registered++;
                }
                catch (TargetInvocationException ex)
                {
                    // One bad row must not cost the others theirs.
                    _logger.LogWarning(
                        ex.InnerException ?? ex,
                        "Curator: Home Screen Sections rejected the registration for '{Name}'",
                        section.Name);
                }
            }

            if (registered == 0 && sections.Count > 0)
            {
                // Nothing got through. Almost always the other plugin being asked
                // before it has finished starting — its entry point reaches for a
                // service provider it has not built yet. Reported as unreachable
                // rather than as a count of zero, so the caller can retry or fall
                // back instead of believing it published rows.
                _logger.LogWarning(
                    "Curator: Home Screen Sections accepted none of {Count} row registration(s); treating it as unavailable",
                    sections.Count);
                return null;
            }

            _logger.LogInformation("Curator: registered {Count} home screen row(s) directly with Home Screen Sections", registered);
            return registered;
        }

        /// <summary>
        /// Finds the registration method, and a way to build the argument it wants.
        /// </summary>
        /// <remarks>
        /// The parameter is a Newtonsoft <c>JObject</c>, which Curator has no
        /// reference to and does not want one — so the factory is built from the
        /// parameter's own type by calling its static <c>Parse</c>. That works
        /// whichever copy of Newtonsoft the other plugin loaded, and keeps working
        /// if it swaps one for another.
        /// </remarks>
        private (MethodInfo Register, Func<string, object> PayloadFactory)? ResolveEntryPoint()
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(AssemblyFragment, StringComparison.OrdinalIgnoreCase) == true);

            if (assembly is null)
            {
                _logger.LogWarning(
                    "Curator: the Home Screen Sections plugin is not loaded, so categories cannot appear as home screen rows. "
                    + "Playlists were still created and are available under Playlists.");
                return null;
            }

            var pluginInterface = assembly.GetType(PluginInterfaceTypeName);
            var register = pluginInterface?.GetMethod(RegisterMethodName, BindingFlags.Public | BindingFlags.Static);
            if (register is null)
            {
                _logger.LogWarning(
                    "Curator: Home Screen Sections is installed but exposes no {Type}.{Method}; it is probably older than Curator expects. "
                    + "Switch the home screen setting to Collection Sections, or update it.",
                    PluginInterfaceTypeName,
                    RegisterMethodName);
                return null;
            }

            var parameters = register.GetParameters();
            var parse = parameters.Length == 1
                ? parameters[0].ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
                : null;

            if (parse is null)
            {
                _logger.LogWarning(
                    "Curator: Home Screen Sections' {Method} does not take a payload Curator knows how to build; rows cannot be registered directly",
                    RegisterMethodName);
                return null;
            }

            return (register, json => parse.Invoke(null, [json])
                ?? throw new InvalidOperationException("Curator: the section registration payload could not be parsed."));
        }
    }
}
