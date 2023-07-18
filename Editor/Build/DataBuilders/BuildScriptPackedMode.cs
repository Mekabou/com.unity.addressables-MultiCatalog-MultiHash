using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.Build.Pipeline;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.Util;
using static UnityEditor.AddressableAssets.Build.ContentUpdateScript;

#if UNITY_2022_2_OR_NEWER
using UnityEditor.AddressableAssets.BuildReportVisualizer;
#endif

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Build scripts used for player builds and running with bundles in the editor.
    /// </summary>
    [CreateAssetMenu(fileName = "BuildScriptPacked.asset", menuName = "Addressables/Content Builders/Default Build Script")]
    public class BuildScriptPackedMode : BuildScriptBase
    {
        /// <inheritdoc />
        public override string Name
        {
            get { return "Default Build Script"; }
        }

        internal List<ObjectInitializationData> m_ResourceProviderData;
        List<AssetBundleBuild> m_AllBundleInputDefs;
        Dictionary<AddressableAssetGroup, (string, string)[]> m_GroupToBundleNames;
        HashSet<string> m_CreatedProviderIds;
        UnityEditor.Build.Pipeline.Utilities.LinkXmlGenerator m_Linker;
        Dictionary<string, string> m_BundleToInternalId = new Dictionary<string, string>();
        private string m_CatalogBuildPath;

        internal List<ObjectInitializationData> ResourceProviderData => m_ResourceProviderData.ToList();

        /// <inheritdoc />
        public override bool CanBuildData<T>()
        {
            return typeof(T).IsAssignableFrom(typeof(AddressablesPlayerBuildResult));
        }

        /// <inheritdoc />
        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {

#if UNITY_2022_2_OR_NEWER
            bool buildReportSettingCheck = ProjectConfigData.UserHasBeenInformedAboutBuildReportSettingPreBuild;
            if (!buildReportSettingCheck && !Application.isBatchMode && !ProjectConfigData.GenerateBuildLayout)
            {
                bool turnOnBuildLayout = EditorUtility.DisplayDialog("Addressables Build Report", "There's a new Addressables Build Report you can check out after your content build.  " +
                    "However, this requires that 'Debug Build Layout' is turned on.  The setting can be found in Edit > Preferences > Addressables.  Would you like to turn it on?", "Yes", "No");
                if (turnOnBuildLayout)
                    ProjectConfigData.GenerateBuildLayout = true;
                ProjectConfigData.UserHasBeenInformedAboutBuildReportSettingPreBuild = true;
            }
#endif


            TResult result = default(TResult);
            m_IncludedGroupsInBuild?.Clear();

            InitializeBuildContext(builderInput, out AddressableAssetsBuildContext aaContext);

            using (m_Log.ScopedStep(LogLevel.Info, "ProcessAllGroups"))
            {
                var errorString = ProcessAllGroups(aaContext);
                if (!string.IsNullOrEmpty(errorString))
                    result = CreateErrorResult<TResult>(errorString, builderInput, aaContext);
            }

            if (result == null)
            {
                result = DoBuild<TResult>(builderInput, aaContext);
            }

            if (result != null)
            {
                var span = DateTime.Now - aaContext.buildStartTime;
                result.Duration = span.TotalSeconds;
            }

            if (result != null && string.IsNullOrEmpty(result.Error))
            {
                foreach (var group in m_IncludedGroupsInBuild)
                    ContentUpdateScript.ClearContentUpdateNotifications(group);
            }

            if (result != null && string.IsNullOrEmpty(result.Error))
            {
                foreach (var group in m_IncludedGroupsInBuild)
                    ContentUpdateScript.ClearContentUpdateNotifications(group);
            }

#if UNITY_2022_2_OR_NEWER
            if (result != null && !Application.isBatchMode && ProjectConfigData.AutoOpenAddressablesReport && ProjectConfigData.GenerateBuildLayout)
            {
                BuildReportWindow.ShowWindow();
            }
#endif
            return result;
        }

        /// <summary>
        /// Determines the catalogs to be built.
        ///
        /// The default implementation will build solely the "main" catalog.
        /// </summary>
        /// <param name="builderInput">the builder input</param>
        /// <param name="aaContext">the build context</param>
        /// <returns>a list containing one build info object for each catalog to be built</returns>
        protected virtual List<ContentCatalogBuildInfo> GetContentCatalogs(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            var list = new List<ContentCatalogBuildInfo>(1);
            var catalog = new ContentCatalogBuildInfo(ResourceManagerRuntimeData.kCatalogAddress, builderInput.RuntimeCatalogFilename);
            catalog.BuildPath = Addressables.BuildPath;
            catalog.LoadPath = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
            catalog.Locations.AddRange(aaContext.locations);
            list.Add(catalog);

            m_CatalogBuildPath = Path.Combine(catalog.BuildPath, catalog.CatalogFilename);
            return list;
        }

        private TResult CreateErrorResult<TResult>(string errorString, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext) where TResult : IDataBuilderResult
        {
            BuildLayoutGenerationTask.GenerateErrorReport(errorString, aaContext, builderInput.PreviousContentState);
            return AddressableAssetBuildResult.CreateResult<TResult>(null, 0, errorString);
        }

        internal const string UserHasBeenInformedAboutBuildReportSettingPreBuild = nameof(UserHasBeenInformedAboutBuildReportSettingPreBuild);

        internal void InitializeBuildContext(AddressablesDataBuilderInput builderInput, out AddressableAssetsBuildContext aaContext)
        {
            var now = DateTime.Now;
            var aaSettings = builderInput.AddressableSettings;

            m_AllBundleInputDefs = new List<AssetBundleBuild>();
            m_GroupToBundleNames = new Dictionary<AddressableAssetGroup, (string, string)[]>();
            var bundleToAssetGroup = new Dictionary<string, string>();
            var runtimeData = new ResourceManagerRuntimeData
            {
                SettingsHash = aaSettings.currentHash.ToString(),
                CertificateHandlerType = aaSettings.CertificateHandlerType,
                BuildTarget = builderInput.Target.ToString(),
#if ENABLE_CCD
                CcdManagedData = aaSettings.m_CcdManagedData,
#endif
                ProfileEvents = builderInput.ProfilerEventsEnabled,
                LogResourceManagerExceptions = aaSettings.buildSettings.LogResourceManagerExceptions,
                DisableCatalogUpdateOnStartup = aaSettings.DisableCatalogUpdateOnStartup,
                IsLocalCatalogInBundle = aaSettings.BundleLocalCatalog,
#if UNITY_2019_3_OR_NEWER
                AddressablesVersion = PackageManager.PackageInfo.FindForAssembly(typeof(Addressables).Assembly)?.version,
#endif
                MaxConcurrentWebRequests = aaSettings.MaxConcurrentWebRequests,
                CatalogRequestsTimeout = aaSettings.CatalogRequestsTimeout
            };
            m_Linker = UnityEditor.Build.Pipeline.Utilities.LinkXmlGenerator.CreateDefault();
            m_Linker.AddAssemblies(new[] { typeof(Addressables).Assembly, typeof(UnityEngine.ResourceManagement.ResourceManager).Assembly });
            m_Linker.AddTypes(runtimeData.CertificateHandlerType);

            m_ResourceProviderData = new List<ObjectInitializationData>();
            aaContext = new AddressableAssetsBuildContext
            {
                Settings = aaSettings,
                runtimeData = runtimeData,
                bundleToAssetGroup = bundleToAssetGroup,
                locations = new List<ContentCatalogDataEntry>(),
                providerTypes = new HashSet<Type>(),
                assetEntries = new List<AddressableAssetEntry>(),
                buildStartTime = now
            };

            m_CreatedProviderIds = new HashSet<string>();
        }

        struct SBPSettingsOverwriterScope : IDisposable
        {
            bool m_PrevSlimResults;

            public SBPSettingsOverwriterScope(bool forceFullWriteResults)
            {
                m_PrevSlimResults = ScriptableBuildPipeline.slimWriteResults;
                if (forceFullWriteResults)
                    ScriptableBuildPipeline.slimWriteResults = false;
            }

            public void Dispose()
            {
                ScriptableBuildPipeline.slimWriteResults = m_PrevSlimResults;
            }
        }

        internal static string GetBuiltInShaderBundleNamePrefix(AddressableAssetsBuildContext aaContext)
        {
            return GetBuiltInShaderBundleNamePrefix(aaContext.Settings);
        }

        internal static string GetBuiltInShaderBundleNamePrefix(AddressableAssetSettings settings)
        {
            string value = "";
            switch (settings.ShaderBundleNaming)
            {
                case ShaderBundleNaming.DefaultGroupGuid:
                    value = settings.DefaultGroup.Guid;
                    break;
                case ShaderBundleNaming.ProjectName:
                    value = Hash128.Compute(GetProjectName()).ToString();
                    break;
                case ShaderBundleNaming.Custom:
                    value = settings.ShaderBundleCustomNaming;
                    break;
            }

            return value;
        }

        void AddBundleProvider(BundledAssetGroupSchema schema)
        {
            var bundleProviderId = schema.GetBundleCachedProviderId();

            if (!m_CreatedProviderIds.Contains(bundleProviderId))
            {
                m_CreatedProviderIds.Add(bundleProviderId);
                var bundleProviderType = schema.AssetBundleProviderType.Value;
                var bundleProviderData = ObjectInitializationData.CreateSerializedInitializationData(bundleProviderType, bundleProviderId);
                m_ResourceProviderData.Add(bundleProviderData);
            }
        }

        internal static string GetMonoScriptBundleNamePrefix(AddressableAssetsBuildContext aaContext)
        {
            return GetMonoScriptBundleNamePrefix(aaContext.Settings);
        }

        internal static string GetMonoScriptBundleNamePrefix(AddressableAssetSettings settings)
        {
            string value = null;
            switch (settings.MonoScriptBundleNaming)
            {
                case MonoScriptBundleNaming.ProjectName:
                    value = Hash128.Compute(GetProjectName()).ToString();
                    break;
                case MonoScriptBundleNaming.DefaultGroupGuid:
                    value = settings.DefaultGroup.Guid;
                    break;
                case MonoScriptBundleNaming.Custom:
                    value = settings.MonoScriptBundleCustomNaming;
                    break;
            }

            return value;
        }

        /// <summary>
        /// The method that does the actual building after all the groups have been processed.
        /// </summary>
        /// <param name="builderInput">The generic builderInput of the</param>
        /// <param name="aaContext"></param>
        /// <typeparam name="TResult"></typeparam>
        /// <returns></returns>
        protected virtual TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext) where TResult : IDataBuilderResult
        {
            var genericResult = AddressableAssetBuildResult.CreateResult<TResult>();
            AddressablesPlayerBuildResult addrResult = genericResult as AddressablesPlayerBuildResult;

            ExtractDataTask extractData = new ExtractDataTask();
            ContentUpdateContext contentUpdateContext = default;
            List<CachedAssetState> carryOverCachedState = new List<CachedAssetState>();
            var tempPath = Path.GetDirectoryName(Application.dataPath) + "/" + Addressables.LibraryPath + PlatformMappingService.GetPlatformPathSubFolder() + "/addressables_content_state.bin";

            var bundleRenameMap = new Dictionary<string, string>();
            var playerBuildVersion = builderInput.PlayerVersion;
            if (m_AllBundleInputDefs.Count > 0)
            {
                if (!BuildUtility.CheckModifiedScenesAndAskToSave())
                    return CreateErrorResult<TResult>("Unsaved scenes", builderInput, aaContext);

                var buildTarget = builderInput.Target;
                var buildTargetGroup = builderInput.TargetGroup;

                var buildParams = new AddressableAssetsBundleBuildParameters(
                    aaContext.Settings,
                    aaContext.bundleToAssetGroup,
                    buildTarget,
                    buildTargetGroup,
                    aaContext.Settings.buildSettings.bundleBuildPath);

                var builtinShaderBundleName = GetBuiltInShaderBundleNamePrefix(aaContext) + "_unitybuiltinshaders.bundle";

                var schema = aaContext.Settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>();
                AddBundleProvider(schema);

                string monoScriptBundleName = GetMonoScriptBundleNamePrefix(aaContext);
                if (!string.IsNullOrEmpty(monoScriptBundleName))
                    monoScriptBundleName += "_monoscripts.bundle";
                var buildTasks = RuntimeDataBuildTasks(builtinShaderBundleName, monoScriptBundleName);
                buildTasks.Add(extractData);

                IBundleBuildResults results;
                using (m_Log.ScopedStep(LogLevel.Info, "ContentPipeline.BuildAssetBundles"))
                using (new SBPSettingsOverwriterScope(ProjectConfigData.GenerateBuildLayout)) // build layout generation requires full SBP write results
                {
                    var buildContent = new BundleBuildContent(m_AllBundleInputDefs);
                    var exitCode = ContentPipeline.BuildAssetBundles(buildParams, buildContent, out results, buildTasks, aaContext, m_Log);

                    if (exitCode < ReturnCode.Success)
                        return CreateErrorResult<TResult>("SBP Error" + exitCode, builderInput, aaContext);
                }

                var groups = aaContext.Settings.groups.Where(g => g != null);

                var postCatalogUpdateCallbacks = new List<Action>();
                using (m_Log.ScopedStep(LogLevel.Info, "PostProcessBundles"))
                using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
                {
                    progressTracker.UpdateTask("Post Processing AssetBundles");

                    foreach (var assetGroup in groups)
                    {
                        if (!aaContext.assetGroupToBundles.ContainsKey(assetGroup))
                            continue;

                        using (m_Log.ScopedStep(LogLevel.Info, assetGroup.name))
                        {
                            PostProcessBundles(assetGroup, results, addrResult,
                                builderInput.Registry, aaContext,
                                bundleRenameMap, postCatalogUpdateCallbacks);
                        }
                    }
                }

                using (m_Log.ScopedStep(LogLevel.Info, "Process Catalog Entries"))
                {
                    Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap = BuildLocationIdToCatalogEntryMap(aaContext.locations);
                    if (builderInput.PreviousContentState != null)
                    {
                        contentUpdateContext = new ContentUpdateContext()
                        {
                            BundleToInternalBundleIdMap = m_BundleToInternalId,
                            GuidToPreviousAssetStateMap = BuildGuidToCachedAssetStateMap(builderInput.PreviousContentState, aaContext.Settings),
                            IdToCatalogDataEntryMap = locationIdToCatalogEntryMap,
                            WriteData = extractData.WriteData,
                            ContentState = builderInput.PreviousContentState,
                            Registry = builderInput.Registry,
                            PreviousAssetStateCarryOver = carryOverCachedState
                        };
                    }
                    ProcessCatalogEntriesForBuild(aaContext, groups, builderInput, extractData.WriteData,
                        contentUpdateContext, m_BundleToInternalId, locationIdToCatalogEntryMap);
                    foreach (var postUpdateCatalogCallback in postCatalogUpdateCallbacks)
                        postUpdateCatalogCallback.Invoke();

                    foreach (var r in results.WriteResults)
                    {
                        var resultValue = r.Value;
                        m_Linker.AddTypes(resultValue.includedTypes);
#if UNITY_2021_1_OR_NEWER
                        m_Linker.AddSerializedClass(resultValue.includedSerializeReferenceFQN);
#else
                        if (resultValue.GetType().GetProperty("includedSerializeReferenceFQN") != null)
                            m_Linker.AddSerializedClass(resultValue.GetType().GetProperty("includedSerializeReferenceFQN").GetValue(resultValue) as System.Collections.Generic.IEnumerable<string>);
#endif
                    }
                }
            }

            var resourceProviderData = new List<ObjectInitializationData>(m_ResourceProviderData);
            foreach (var t in aaContext.providerTypes)
            {
                resourceProviderData.Add(ObjectInitializationData.CreateSerializedInitializationData(t));
            }

            var instanceProviderData = ObjectInitializationData.CreateSerializedInitializationData(instanceProviderType.Value);
            var sceneProviderData = ObjectInitializationData.CreateSerializedInitializationData(sceneProviderType.Value);

            var contentCatalogs = new List<ContentCatalogData>();
#if ENABLE_BINARY_CATALOG
            using (m_Log.ScopedStep(LogLevel.Info, "Generate Binary Catalog"))
            {
                foreach(var catalogInfo in GetContentCatalogs(builderInput, aaContext))
                {
                    var contentCatalog = new ContentCatalogData(ResourceManagerRuntimeData.kCatalogAddress);
                    contentCatalogs.Add(contentCatalog);

                    if (addrResult != null)
                    {
                        List<object> hashingObjects = new List<object>(addrResult.AssetBundleBuildResults.Count);
                        for (int i = 0; i < addrResult.AssetBundleBuildResults.Count; ++i)
                        {
                            var hashingObject = addrResult.AssetBundleBuildResults[i];
                            if (catalogInfo.Locations.Exists(l => (l.ResourceType == typeof(IAssetBundleResource)) && l.InternalId.Equals(hashingObject.FilePath)))
                            {
                                hashingObjects.Add(addrResult.AssetBundleBuildResults[i].Hash);
                            }
                        }

                        string buildResultHash = HashingMethods.Calculate(hashingObjects.ToArray()).ToString();
                        contentCatalog.m_BuildResultHash = buildResultHash;
                    }

                    contentCatalog.SetData(catalogInfo.Locations.OrderBy(f => f.InternalId).ToList());
                    contentCatalog.ResourceProviderData.AddRange(resourceProviderData);
                    contentCatalog.InstanceProviderData = instanceProviderData;
                    contentCatalog.SceneProviderData = sceneProviderData;

                    var bytes = contentCatalog.SerializeToByteArray();
                    var contentHash = HashingMethods.Calculate(bytes).ToString();

                    if (aaContext.Settings.BuildRemoteCatalog || ProjectConfigData.GenerateBuildLayout)
                        contentCatalog.localHash = contentHash;

                    CreateCatalogFiles(bytes, builderInput, aaContext, contentHash, catalogInfo);
                }
            }
#else
            using (m_Log.ScopedStep(LogLevel.Info, "Generate JSON Catalog"))
            {
                // Use BuildScriptPackedMultiCatalogMode.GetContentCatalogs(a override method) to get catalogInfo in List<ContentCatalogBuildInfo>
                foreach (var catalogInfo in BuildScriptPackedMultiCatalogMode.buildScriptPackedMulti.GetContentCatalogs(builderInput, aaContext))
                {
                    var contentCatalog = new ContentCatalogData(catalogInfo.Identifier);
                    contentCatalogs.Add(contentCatalog);

                    if (addrResult != null)
                    {
                        List<object> hashingObjects = new List<object>(addrResult.AssetBundleBuildResults.Count);
                        for (int i = 0; i < addrResult.AssetBundleBuildResults.Count; ++i)
                        {
                            var hashingObject = addrResult.AssetBundleBuildResults[i];
                            if (catalogInfo.Locations.Exists(l => (l.ResourceType == typeof(IAssetBundleResource)) && l.InternalId.Equals(hashingObject.FilePath)))
                            {
                                hashingObjects.Add(addrResult.AssetBundleBuildResults[i].Hash);
                            }
                        }

                        string buildResultHash = HashingMethods.Calculate(hashingObjects.ToArray()).ToString();
                        contentCatalog.m_BuildResultHash = buildResultHash;
                    }


                    contentCatalog.SetData(catalogInfo.Locations.OrderBy(f => f.InternalId).ToList());
                    contentCatalog.ResourceProviderData.AddRange(resourceProviderData);
                    contentCatalog.InstanceProviderData = instanceProviderData;
                    contentCatalog.SceneProviderData = sceneProviderData;

                    string jsonText = null;
                    string contentHash = null;
                    using (m_Log.ScopedStep(LogLevel.Info, "Generating Json"))
                        jsonText = JsonUtility.ToJson(contentCatalog);

                    if (aaContext.Settings.BuildRemoteCatalog || ProjectConfigData.GenerateBuildLayout)
                    {
                        using (m_Log.ScopedStep(LogLevel.Info, "Hashing Catalog"))
                            contentHash = HashingMethods.Calculate(jsonText).ToString();
                    }

                    CreateCatalogFiles(jsonText, builderInput, aaContext, contentHash, catalogInfo);
                }
            }
#endif

            using (m_Log.ScopedStep(LogLevel.Info, "Generate link"))
            {
                foreach (var pd in resourceProviderData)
                {
                    m_Linker.AddTypes(pd.ObjectType.Value);
                    m_Linker.AddTypes(pd.GetRuntimeTypes());
                }

                m_Linker.AddTypes(instanceProviderData.ObjectType.Value);
                m_Linker.AddTypes(instanceProviderData.GetRuntimeTypes());
                m_Linker.AddTypes(sceneProviderData.ObjectType.Value);
                m_Linker.AddTypes(sceneProviderData.GetRuntimeTypes());

                foreach (var io in aaContext.Settings.InitializationObjects)
                {
                    var provider = io as IObjectInitializationDataProvider;
                    if (provider != null)
                    {
                        var id = provider.CreateObjectInitializationData();
                        aaContext.runtimeData.InitializationObjects.Add(id);
                        m_Linker.AddTypes(id.ObjectType.Value);
                        m_Linker.AddTypes(id.GetRuntimeTypes());
                    }
                }

                m_Linker.AddTypes(typeof(Addressables));
                Directory.CreateDirectory(Addressables.BuildPath + "/AddressablesLink/");
                m_Linker.Save(Addressables.BuildPath + "/AddressablesLink/link.xml");
            }

            var settingsPath = Addressables.BuildPath + "/" + builderInput.RuntimeSettingsFilename;

            using (m_Log.ScopedStep(LogLevel.Info, "Generate Settings"))
                WriteFile(settingsPath, JsonUtility.ToJson(aaContext.runtimeData), builderInput.Registry);

            if (extractData.BuildCache != null && builderInput.PreviousContentState == null)
            {
                using (m_Log.ScopedStep(LogLevel.Info, "Generate Content Update State"))
                {
                    var remoteCatalogLoadPath = aaContext.Settings.BuildRemoteCatalog
                        ? aaContext.Settings.RemoteCatalogLoadPath.GetValue(aaContext.Settings)
                        : string.Empty;

                    var allEntries = new List<AddressableAssetEntry>();
                    using (m_Log.ScopedStep(LogLevel.Info, "Get Assets"))
                        aaContext.Settings.GetAllAssets(allEntries, false, ContentUpdateScript.GroupFilter);

                    if (ContentUpdateScript.SaveContentState(aaContext.locations, aaContext.GuidToCatalogLocation, tempPath, allEntries,
                            extractData.DependencyData, playerBuildVersion, remoteCatalogLoadPath,
                            carryOverCachedState))
                    {
                        string contentStatePath = ContentUpdateScript.GetContentStateDataPath(false, aaContext.Settings);
                        if (ResourceManagerConfig.ShouldPathUseWebRequest(contentStatePath))
                        {
#if ENABLE_CCD
                            contentStatePath = Path.Combine(aaContext.Settings.RemoteCatalogBuildPath.GetValue(aaContext.Settings), Path.GetFileName(tempPath));
#else
                            contentStatePath = ContentUpdateScript.PreviousContentStateFileCachePath;
#endif
                        }

                        try
                        {
                            string directory = Path.GetDirectoryName(contentStatePath);
                            if (!Directory.Exists(directory))
                                Directory.CreateDirectory(directory);
                            if (File.Exists(contentStatePath))
                                File.Delete(contentStatePath);

                            File.Copy(tempPath, contentStatePath, true);
                            if (addrResult != null)
                                addrResult.ContentStateFilePath = contentStatePath;
                            builderInput.Registry.AddFile(contentStatePath);
                        }
                        catch (UnauthorizedAccessException uae)
                        {
                            if (!AddressableAssetUtility.IsVCAssetOpenForEdit(contentStatePath))
                                Debug.LogErrorFormat("Cannot access the file {0}. It may be locked by version control.",
                                    contentStatePath);
                            else
                                Debug.LogException(uae);
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                }
            }

            if (addrResult != null)
                addrResult.IsUpdateContentBuild = builderInput.PreviousContentState != null;

            genericResult.LocationCount = aaContext.locations.Count;
            genericResult.OutputPath = settingsPath;

            if (ProjectConfigData.GenerateBuildLayout)
            {
                using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
                {
                    progressTracker.UpdateTask("Generating Build Layout");
                    using (m_Log.ScopedStep(LogLevel.Info, "Generate Build Layout"))
                    {
                        foreach (var contentCatalog in contentCatalogs)
                        {
                            List<IBuildTask> tasks = new List<IBuildTask>();
                            var buildLayoutTask = new BuildLayoutGenerationTask();
                            buildLayoutTask.m_BundleNameRemap = bundleRenameMap;
                            buildLayoutTask.m_ContentCatalogData = contentCatalog;
                            if (contentUpdateContext.ContentState != null)
                                buildLayoutTask.m_AddressablesInput = builderInput;
                            tasks.Add(buildLayoutTask);
                            BuildTasksRunner.Run(tasks, extractData.m_BuildContext);
                        }
                    }
                }
            }

            return genericResult;
        }

        private static void ProcessCatalogEntriesForBuild(AddressableAssetsBuildContext aaContext,
            IEnumerable<AddressableAssetGroup> validGroups, AddressablesDataBuilderInput builderInput, IBundleWriteData writeData,
            ContentUpdateContext contentUpdateContext, Dictionary<string, string> bundleToInternalId, Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap)
        {
            using (var progressTracker = new UnityEditor.Build.Pipeline.Utilities.ProgressTracker())
            {
                progressTracker.UpdateTask("Post Processing Catalog Entries");
                if (builderInput.PreviousContentState != null)
                {
                    RevertUnchangedAssetsToPreviousAssetState.Run(aaContext, contentUpdateContext);
                }
                else
                {
                    foreach (var assetGroup in validGroups)
                        SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId(assetGroup.entries, bundleToInternalId, writeData, locationIdToCatalogEntryMap);
                }
            }

            bundleToInternalId.Clear();
        }

        private static Dictionary<string, ContentCatalogDataEntry> BuildLocationIdToCatalogEntryMap(List<ContentCatalogDataEntry> locations)
        {
            Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap = new Dictionary<string, ContentCatalogDataEntry>();
            foreach (var location in locations)
                locationIdToCatalogEntryMap[location.InternalId] = location;

            return locationIdToCatalogEntryMap;
        }

        private static Dictionary<string, CachedAssetState> BuildGuidToCachedAssetStateMap(AddressablesContentState contentState, AddressableAssetSettings settings)
        {
            Dictionary<string, CachedAssetState> addressableEntryToCachedStateMap = new Dictionary<string, CachedAssetState>();
            foreach (var cachedInfo in contentState.cachedInfos)
                addressableEntryToCachedStateMap[cachedInfo.asset.guid.ToString()] = cachedInfo;

            return addressableEntryToCachedStateMap;
        }

#if ENABLE_BINARY_CATALOG
        internal bool CreateCatalogFiles(byte[] data, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext, string catalogHash, ContentCatalogBuildInfo buildInfo)
        {
            if (data == null || data.Length == 0 || builderInput == null || aaContext == null)
            {
                Addressables.LogError("Unable to create content catalog (Null arguments).");
                return false;
            }

            // Path needs to be resolved at runtime.
            string loadPath = $"{buildInfo.LoadPath}/{buildInfo.CatalogFilename}";
            string buildPath = Path.Combine(buildInfo.BuildPath, buildInfo.CatalogFilename);

            if (aaContext.Settings.BundleLocalCatalog)
            {
                loadPath = loadPath.Replace(".bin", ".bundle");
                buildPath = buildPath.Replace(".bin", ".bundle");
                var returnCode = CreateCatalogBundle(buildPath, data, builderInput);
                if (returnCode != ReturnCode.Success || !File.Exists(buildPath))
                {
                    Addressables.LogError($"An error occurred during the creation of the content catalog bundle (return code {returnCode}).");
                    return false;
                }
            }
            else
            {
                WriteFile(buildPath, data, builderInput.Registry);
            }

            string[] dependencyHashes = null;
            if (aaContext.Settings.BuildRemoteCatalog)
            {
                if (buildInfo != null)
                {
                    dependencyHashes = CreateRemoteCatalog(data, aaContext.runtimeData.CatalogLocations, aaContext.Settings, builderInput, new ProviderLoadRequestOptions() { IgnoreFailures = true }, catalogHash);
                }
                else
                {
                    Addressables.LogError($"The {nameof(aaContext.Settings.BuildRemoteCatalog)} setting is currently not supported in this package.");
                    return false;
                }
            }

            aaContext.runtimeData.CatalogLocations.Add(new ResourceLocationData(
                new[] { ResourceManagerRuntimeData.kCatalogAddress },
                loadPath,
                typeof(ContentCatalogProvider),
                typeof(ContentCatalogData),
                dependencyHashes));

            return true;
        }

        static string[] CreateRemoteCatalog(byte[] data, List<ResourceLocationData> locations, AddressableAssetSettings aaSettings, AddressablesDataBuilderInput builderInput,
            ProviderLoadRequestOptions catalogLoadOptions, string contentHash)
        {
            string[] dependencyHashes = null;

            var versionedFileName = aaSettings.profileSettings.EvaluateString(aaSettings.activeProfileId, "/catalog_" + builderInput.PlayerVersion);
            var remoteBuildFolder = aaSettings.RemoteCatalogBuildPath.GetValue(aaSettings);
            var remoteLoadFolder = aaSettings.RemoteCatalogLoadPath.GetValue(aaSettings);

            if (string.IsNullOrEmpty(remoteBuildFolder) ||
                string.IsNullOrEmpty(remoteLoadFolder) ||
                remoteBuildFolder == AddressableAssetProfileSettings.undefinedEntryValue ||
                remoteLoadFolder == AddressableAssetProfileSettings.undefinedEntryValue)
            {
                Addressables.LogWarning(
                    "Remote Build and/or Load paths are not set on the main AddressableAssetSettings asset, but 'Build Remote Catalog' is true.  Cannot create remote catalog.  In the inspector for any group, double click the 'Addressable Asset Settings' object to begin inspecting it. '" +
                    remoteBuildFolder + "', '" + remoteLoadFolder + "'");
            }
            else
            {
                var remoteJsonBuildPath = remoteBuildFolder + versionedFileName + ".bin";
                var remoteHashBuildPath = remoteBuildFolder + versionedFileName + ".hash";

                WriteFile(remoteJsonBuildPath, data, builderInput.Registry);
                WriteFile(remoteHashBuildPath, contentHash, builderInput.Registry);

                dependencyHashes = new string[((int)ContentCatalogProvider.DependencyHashIndex.Count)];
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] = ResourceManagerRuntimeData.kCatalogAddress + "RemoteHash";
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] = ResourceManagerRuntimeData.kCatalogAddress + "CacheHash";

                var remoteHashLoadPath = remoteLoadFolder + versionedFileName + ".hash";
                var remoteHashLoadLocation = new ResourceLocationData(
                    new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] },
                    remoteHashLoadPath,
                    typeof(TextDataProvider), typeof(string));
                remoteHashLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(remoteHashLoadLocation);

#if UNITY_SWITCH
                var cacheLoadPath = remoteHashLoadPath; // ResourceLocationBase does not allow empty string id
#else
                var cacheLoadPath = "{UnityEngine.Application.persistentDataPath}/com.unity.addressables" + versionedFileName + ".hash";
#endif
                var cacheLoadLocation = new ResourceLocationData(
                    new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] },
                    cacheLoadPath,
                    typeof(TextDataProvider), typeof(string));
                cacheLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(cacheLoadLocation);
            }

            return dependencyHashes;
        }

        internal ReturnCode CreateCatalogBundle(string filepath, byte[] data, AddressablesDataBuilderInput builderInput)
        {
            if (string.IsNullOrEmpty(filepath) || data == null || data.Length == 0 || builderInput == null)
            {
                throw new ArgumentException("Unable to create catalog bundle (null arguments).");
            }

            // A bundle requires an actual asset
            var tempFolderName = "TempCatalogFolder";

            var configFolder = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder;
            if (builderInput.AddressableSettings != null && builderInput.AddressableSettings.IsPersisted)
                configFolder = builderInput.AddressableSettings.ConfigFolder;

            var tempFolderPath = Path.Combine(configFolder, tempFolderName);
            var tempFilePath = Path.Combine(tempFolderPath, Path.GetFileName(filepath).Replace(".bundle", ".bin"));
            if (!WriteFile(tempFilePath, data, builderInput.Registry))
            {
                throw new Exception("An error occurred during the creation of temporary files needed to bundle the content catalog.");
            }

            AssetDatabase.Refresh();

            var bundleBuildContent = new BundleBuildContent(new[]
            {
                new AssetBundleBuild()
                {
                    assetBundleName = Path.GetFileName(filepath),
                    assetNames = new[] {tempFilePath},
                    addressableNames = new string[0]
                }
            });

            var buildTasks = new List<IBuildTask>
            {
                new CalculateAssetDependencyData(),
                new GenerateBundlePacking(),
                new GenerateBundleCommands(),
                new WriteSerializedFiles(),
                new ArchiveAndCompressBundles()
            };

            var buildParams = new BundleBuildParameters(builderInput.Target, builderInput.TargetGroup, Path.GetDirectoryName(filepath));
            if (builderInput.Target == BuildTarget.WebGL)
                buildParams.BundleCompression = BuildCompression.LZ4Runtime;
            var retCode = ContentPipeline.BuildAssetBundles(buildParams, bundleBuildContent, out IBundleBuildResults result, buildTasks, m_Log);

            if (Directory.Exists(tempFolderPath))
            {
                Directory.Delete(tempFolderPath, true);
                builderInput.Registry.RemoveFile(tempFilePath);
            }

            var tempFolderMetaFile = tempFolderPath + ".meta";
            if (File.Exists(tempFolderMetaFile))
            {
                File.Delete(tempFolderMetaFile);
                builderInput.Registry.RemoveFile(tempFolderMetaFile);
            }

            if (File.Exists(filepath))
            {
                builderInput.Registry.AddFile(filepath);
            }

            return retCode;
        }

#else
        internal bool CreateCatalogFiles(string jsonText, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext, string catalogHash, ContentCatalogBuildInfo buildInfo)
        {
            if (string.IsNullOrEmpty(jsonText) || builderInput == null || aaContext == null)
            {
                Addressables.LogError("Unable to create content catalog (Null arguments).");
                return false;
            }

            // Path needs to be resolved at runtime.
            string loadPath = $"{buildInfo.LoadPath}/{buildInfo.CatalogFilename}";
            string buildPath = Path.Combine(buildInfo.BuildPath, buildInfo.CatalogFilename);

            if (aaContext.Settings.BundleLocalCatalog)
            {
                loadPath = loadPath.Replace(".json", ".bundle");
                buildPath = buildPath.Replace(".json", ".bundle");
                var returnCode = CreateCatalogBundle(buildPath, jsonText, builderInput);
                if (returnCode != ReturnCode.Success || !File.Exists(buildPath))
                {
                    Addressables.LogError($"An error occurred during the creation of the content catalog bundle (return code {returnCode}).");
                    return false;
                }
            }
            else
            {
                WriteFile(buildPath, jsonText, builderInput.Registry);
            }

            string[] dependencyHashes = null;           
            if (aaContext.Settings.BuildRemoteCatalog)
            {
                if (buildInfo != null)
                {
                    // Get the CatalogFilename with out .json, CatalogFilename = ContentCatalogBuildInfo.CatalogFilename.
                    string catalogName = buildInfo.CatalogFilename.Split(".json", StringSplitOptions.RemoveEmptyEntries)[0];

                    // Use dependencyHashes only contains external hashes as string[] dependencyHashes, don't create defult catalog.hash and catalog.json.
                    dependencyHashes = BuildScriptPackedMultiCatalogMode.buildScriptPackedMulti.CreateRemoteHash(jsonText, aaContext.runtimeData.CatalogLocations, aaContext.Settings, builderInput, new ProviderLoadRequestOptions() { IgnoreFailures = true }, catalogHash, catalogName);

                    // Defult way to create remote catalog.
                    //dependencyHashes = CreateRemoteCatalog(jsonText, aaContext.runtimeData.CatalogLocations, aaContext.Settings, builderInput, new ProviderLoadRequestOptions() { IgnoreFailures = true }, catalogHash);              
                }
                else
                {
                    Addressables.LogError($"The {nameof(aaContext.Settings.BuildRemoteCatalog)} setting is currently not supported in this package.");
                    return false;
                }
            }

            if (buildInfo?.Register ?? true)
            {
                aaContext.runtimeData.CatalogLocations.Add(new ResourceLocationData(
                new[] { ResourceManagerRuntimeData.kCatalogAddress },
                    loadPath,
                    typeof(ContentCatalogProvider),
                    typeof(ContentCatalogData),
                    dependencyHashes));
            }

            return true;
        }

        internal ReturnCode CreateCatalogBundle(string filepath, string jsonText, AddressablesDataBuilderInput builderInput)
        {
            if (string.IsNullOrEmpty(filepath) || string.IsNullOrEmpty(jsonText) || builderInput == null)
            {
                throw new ArgumentException("Unable to create catalog bundle (null arguments).");
            }

            // A bundle requires an actual asset
            var tempFolderName = "TempCatalogFolder";

            var configFolder = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder;
            if (builderInput.AddressableSettings != null && builderInput.AddressableSettings.IsPersisted)
                configFolder = builderInput.AddressableSettings.ConfigFolder;

            var tempFolderPath = Path.Combine(configFolder, tempFolderName);
            var tempFilePath = Path.Combine(tempFolderPath, Path.GetFileName(filepath).Replace(".bundle", ".json"));
            if (!WriteFile(tempFilePath, jsonText, builderInput.Registry))
            {
                throw new Exception("An error occurred during the creation of temporary files needed to bundle the content catalog.");
            }

            AssetDatabase.Refresh();

            var bundleBuildContent = new BundleBuildContent(new[]
            {
                new AssetBundleBuild()
                {
                    assetBundleName = Path.GetFileName(filepath),
                    assetNames = new[] {tempFilePath},
                    addressableNames = new string[0]
                }
            });

            var buildTasks = new List<IBuildTask>
            {
                new CalculateAssetDependencyData(),
                new GenerateBundlePacking(),
                new GenerateBundleCommands(),
                new WriteSerializedFiles(),
                new ArchiveAndCompressBundles()
            };

            var buildParams = new BundleBuildParameters(builderInput.Target, builderInput.TargetGroup, Path.GetDirectoryName(filepath));
            if (builderInput.Target == BuildTarget.WebGL)
                buildParams.BundleCompression = BuildCompression.LZ4Runtime;
            var retCode = ContentPipeline.BuildAssetBundles(buildParams, bundleBuildContent, out IBundleBuildResults result, buildTasks, m_Log);

            if (Directory.Exists(tempFolderPath))
            {
                Directory.Delete(tempFolderPath, true);
                builderInput.Registry.RemoveFile(tempFilePath);
            }

            var tempFolderMetaFile = tempFolderPath + ".meta";
            if (File.Exists(tempFolderMetaFile))
            {
                File.Delete(tempFolderMetaFile);
                builderInput.Registry.RemoveFile(tempFolderMetaFile);
            }

            if (File.Exists(filepath))
            {
                builderInput.Registry.AddFile(filepath);
            }

            return retCode;
        }

        public virtual string[] CreateRemoteHash(string jsonText, List<ResourceLocationData> locations, AddressableAssetSettings aaSettings, AddressablesDataBuilderInput builderInput, ProviderLoadRequestOptions catalogLoadOptions, string contentHash, string catalogName)
        {
            string[] dependencyHashes = null;
            return dependencyHashes;
        }

        static string[] CreateRemoteCatalog(string jsonText, List<ResourceLocationData> locations, AddressableAssetSettings aaSettings, AddressablesDataBuilderInput builderInput, ProviderLoadRequestOptions catalogLoadOptions, string contentHash)
        {
            string[] dependencyHashes = null;

            if (string.IsNullOrEmpty(contentHash))
                contentHash = HashingMethods.Calculate(jsonText).ToString();

            var versionedFileName = aaSettings.profileSettings.EvaluateString(aaSettings.activeProfileId, "/catalog_" + builderInput.PlayerVersion);
            var remoteBuildFolder = aaSettings.RemoteCatalogBuildPath.GetValue(aaSettings);
            var remoteLoadFolder = aaSettings.RemoteCatalogLoadPath.GetValue(aaSettings);

            if (string.IsNullOrEmpty(remoteBuildFolder) ||
                string.IsNullOrEmpty(remoteLoadFolder) ||
                remoteBuildFolder == AddressableAssetProfileSettings.undefinedEntryValue ||
                remoteLoadFolder == AddressableAssetProfileSettings.undefinedEntryValue)
            {
                Addressables.LogWarning(
                    "Remote Build and/or Load paths are not set on the main AddressableAssetSettings asset, but 'Build Remote Catalog' is true.  Cannot create remote catalog.  In the inspector for any group, double click the 'Addressable Asset Settings' object to begin inspecting it. '" +
                    remoteBuildFolder + "', '" + remoteLoadFolder + "'");
            }
            else
            {
                var remoteJsonBuildPath = remoteBuildFolder + versionedFileName + ".json";
                var remoteHashBuildPath = remoteBuildFolder + versionedFileName + ".hash";

                WriteFile(remoteJsonBuildPath, jsonText, builderInput.Registry);
                WriteFile(remoteHashBuildPath, contentHash, builderInput.Registry);

                dependencyHashes = new string[((int)ContentCatalogProvider.DependencyHashIndex.Count)];
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] = ResourceManagerRuntimeData.kCatalogAddress + "RemoteHash";
                dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] = ResourceManagerRuntimeData.kCatalogAddress + "CacheHash";

                var remoteHashLoadPath = remoteLoadFolder + versionedFileName + ".hash";
                var remoteHashLoadLocation = new ResourceLocationData(
                    new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] },
                    remoteHashLoadPath,
                    typeof(TextDataProvider), typeof(string));
                remoteHashLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(remoteHashLoadLocation);

#if UNITY_SWITCH
                var cacheLoadPath = remoteHashLoadPath; // ResourceLocationBase does not allow empty string id
#else
                var cacheLoadPath = "{UnityEngine.Application.persistentDataPath}/com.unity.addressables" + versionedFileName + ".hash";
#endif
                var cacheLoadLocation = new ResourceLocationData(
                    new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] },
                    cacheLoadPath,
                    typeof(TextDataProvider), typeof(string));
                cacheLoadLocation.Data = catalogLoadOptions.Copy();
                locations.Add(cacheLoadLocation);
            }

            return dependencyHashes;
        }
#endif

        internal static string GetProjectName()
        {
            return new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name;
        }

        internal static void SetAssetEntriesBundleFileIdToCatalogEntryBundleFileId(ICollection<AddressableAssetEntry> assetEntries, Dictionary<string, string> bundleNameToInternalBundleIdMap, IBundleWriteData writeData, Dictionary<string, ContentCatalogDataEntry> locationIdToCatalogEntryMap)
        {
            foreach (var loc in assetEntries)
            {
                AddressableAssetEntry processedEntry = loc;
                if (loc.IsFolder && loc.SubAssets.Count > 0)
                    processedEntry = loc.SubAssets[0];
                GUID guid = new GUID(processedEntry.guid);
                //For every entry in the write data we need to ensure the BundleFileId is set so we can save it correctly in the cached state
                if (writeData.AssetToFiles.TryGetValue(guid, out List<string> files))
                {
                    string file = files[0];
                    string fullBundleName = writeData.FileToBundle[file];
                    string convertedLocation;

                    if (!bundleNameToInternalBundleIdMap.TryGetValue(fullBundleName, out convertedLocation))
                    {
                        Debug.LogException(new Exception($"Unable to find bundleId for key: {fullBundleName}."));
                    }

                    if (locationIdToCatalogEntryMap.TryGetValue(convertedLocation,
                        out ContentCatalogDataEntry catalogEntry))
                    {
                        loc.BundleFileId = catalogEntry.InternalId;

                        //This is where we strip out the temporary hash added to the bundle name for Content Update for the AssetEntry
                        if (loc.parentGroup?.GetSchema<BundledAssetGroupSchema>()?.BundleNaming ==
                            BundledAssetGroupSchema.BundleNamingStyle.NoHash)
                        {
                            loc.BundleFileId = StripHashFromBundleLocation(loc.BundleFileId);
                        }
                    }
                }
            }
        }

        static string StripHashFromBundleLocation(string hashedBundleLocation)
        {
            return hashedBundleLocation.Remove(hashedBundleLocation.LastIndexOf('_')) + ".bundle";
        }

        /// <inheritdoc />
        protected override string ProcessGroup(AddressableAssetGroup assetGroup, AddressableAssetsBuildContext aaContext)
        {
            if (assetGroup == null)
                return string.Empty;

            if (assetGroup.Schemas.Count == 0)
            {
                Addressables.LogWarning($"{assetGroup.Name} does not have any associated AddressableAssetGroupSchemas. " +
                    $"Data from this group will not be included in the build. " +
                    $"If this is unexpected the AddressableGroup may have become corrupted.");
                return string.Empty;
            }

            foreach (var schema in assetGroup.Schemas)
            {
                var errorString = ProcessGroupSchema(schema, assetGroup, aaContext);
                if (!string.IsNullOrEmpty(errorString))
                    return errorString;
            }

            return string.Empty;
        }

        /// <summary>
        /// Called per group per schema to evaluate that schema.  This can be an easy entry point for implementing the
        ///  build aspects surrounding a custom schema.  Note, you should not rely on schemas getting called in a specific
        ///  order.
        /// </summary>
        /// <param name="schema">The schema to process</param>
        /// <param name="assetGroup">The group this schema was pulled from</param>
        /// <param name="aaContext">The general Addressables build builderInput</param>
        /// <returns></returns>
        protected virtual string ProcessGroupSchema(AddressableAssetGroupSchema schema, AddressableAssetGroup assetGroup, AddressableAssetsBuildContext aaContext)
        {
            var playerDataSchema = schema as PlayerDataGroupSchema;
            if (playerDataSchema != null)
                return ProcessPlayerDataSchema(playerDataSchema, assetGroup, aaContext);
            var bundledAssetSchema = schema as BundledAssetGroupSchema;
            if (bundledAssetSchema != null)
                return ProcessBundledAssetSchema(bundledAssetSchema, assetGroup, aaContext);
            return string.Empty;
        }

        internal string ProcessPlayerDataSchema(
            PlayerDataGroupSchema schema,
            AddressableAssetGroup assetGroup,
            AddressableAssetsBuildContext aaContext)
        {
            if (CreateLocationsForPlayerData(schema, assetGroup, aaContext.locations, aaContext.providerTypes))
            {
                if (!m_CreatedProviderIds.Contains(typeof(LegacyResourcesProvider).Name))
                {
                    m_CreatedProviderIds.Add(typeof(LegacyResourcesProvider).Name);
                    m_ResourceProviderData.Add(ObjectInitializationData.CreateSerializedInitializationData(typeof(LegacyResourcesProvider)));
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// A temporary list of the groups that get processed during a build.
        /// </summary>
        List<AddressableAssetGroup> m_IncludedGroupsInBuild = new List<AddressableAssetGroup>();

        /// <summary>
        /// The processing of the bundled asset schema.  This is where the bundle(s) for a given group are actually setup.
        /// </summary>
        /// <param name="schema">The BundledAssetGroupSchema to process</param>
        /// <param name="assetGroup">The group this schema was pulled from</param>
        /// <param name="aaContext">The general Addressables build builderInput</param>
        /// <returns>The error string, if any.</returns>
        protected virtual string ProcessBundledAssetSchema(
            BundledAssetGroupSchema schema,
            AddressableAssetGroup assetGroup,
            AddressableAssetsBuildContext aaContext)
        {
            if (schema == null || !schema.IncludeInBuild || !assetGroup.entries.Any())
                return string.Empty;

            var errorStr = ErrorCheckBundleSettings(schema, assetGroup, aaContext.Settings);
            if (!string.IsNullOrEmpty(errorStr))
                return errorStr;

            m_IncludedGroupsInBuild?.Add(assetGroup);

            AddBundleProvider(schema);

            var assetProviderId = schema.GetAssetCachedProviderId();
            if (!m_CreatedProviderIds.Contains(assetProviderId))
            {
                m_CreatedProviderIds.Add(assetProviderId);
                var assetProviderType = schema.BundledAssetProviderType.Value;
                var assetProviderData = ObjectInitializationData.CreateSerializedInitializationData(assetProviderType, assetProviderId);
                m_ResourceProviderData.Add(assetProviderData);
            }

#if UNITY_2022_1_OR_NEWER
            string loadPath = schema.LoadPath.GetValue(aaContext.Settings);
            if (loadPath.StartsWith("http://", StringComparison.Ordinal) && PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                Addressables.LogWarning($"Addressable group {assetGroup.Name} uses insecure http for its load path. To allow http connections for UnityWebRequests, change your settings in Edit > Project Settings > Player > Other Settings > Configuration > Allow downloads over HTTP.");
#endif
            if (schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.LZMA && aaContext.runtimeData.BuildTarget == BuildTarget.WebGL.ToString())
                Addressables.LogWarning($"Addressable group {assetGroup.Name} uses LZMA compression, which cannot be decompressed on WebGL. Use LZ4 compression instead.");

            var bundleInputDefs = new List<AssetBundleBuild>();
            var list = PrepGroupBundlePacking(assetGroup, bundleInputDefs, schema);
            aaContext.assetEntries.AddRange(list);
            List<string> uniqueNames = HandleBundleNames(bundleInputDefs, aaContext.bundleToAssetGroup, assetGroup.Guid);
            (string, string)[] groupBundles = new (string, string)[uniqueNames.Count];
            for (int i = 0; i < uniqueNames.Count; ++i)
                groupBundles[i] = (bundleInputDefs[i].assetBundleName, uniqueNames[i]);
            m_GroupToBundleNames.Add(assetGroup, groupBundles);
            m_AllBundleInputDefs.AddRange(bundleInputDefs);
            return string.Empty;
        }

        internal static List<string> HandleBundleNames(List<AssetBundleBuild> bundleInputDefs, Dictionary<string, string> bundleToAssetGroup = null, string assetGroupGuid = null)
        {
            var generatedUniqueNames = new List<string>();
            var handledNames = new HashSet<string>();

            for (int i = 0; i < bundleInputDefs.Count; i++)
            {
                AssetBundleBuild bundleBuild = bundleInputDefs[i];
                string assetBundleName = bundleBuild.assetBundleName;
                if (handledNames.Contains(assetBundleName))
                {
                    int count = 1;
                    var newName = assetBundleName;
                    while (handledNames.Contains(newName) && count < 1000)
                        newName = assetBundleName.Replace(".bundle", string.Format("{0}.bundle", count++));
                    assetBundleName = newName;
                }

                string hashedAssetBundleName = HashingMethods.Calculate(assetBundleName) + ".bundle";
                generatedUniqueNames.Add(assetBundleName);
                handledNames.Add(assetBundleName);

                bundleBuild.assetBundleName = hashedAssetBundleName;
                bundleInputDefs[i] = bundleBuild;

                if (bundleToAssetGroup != null)
                    bundleToAssetGroup.Add(hashedAssetBundleName, assetGroupGuid);
            }

            return generatedUniqueNames;
        }

        internal static string ErrorCheckBundleSettings(BundledAssetGroupSchema schema, AddressableAssetGroup assetGroup, AddressableAssetSettings settings)
        {
            var message = string.Empty;

            string buildPath = settings.profileSettings.GetValueById(settings.activeProfileId, schema.BuildPath.Id);
            string loadPath = settings.profileSettings.GetValueById(settings.activeProfileId, schema.LoadPath.Id);

            if (string.IsNullOrEmpty(buildPath))
            {
                buildPath = schema.BuildPath.Id;
            }

            if (string.IsNullOrEmpty(loadPath))
            {
                loadPath = schema.LoadPath.Id;
            }

            bool buildLocal = AddressableAssetUtility.StringContains(buildPath, "[UnityEngine.AddressableAssets.Addressables.BuildPath]", StringComparison.Ordinal);
            bool loadLocal = AddressableAssetUtility.StringContains(loadPath, "{UnityEngine.AddressableAssets.Addressables.RuntimePath}", StringComparison.Ordinal);

            if (buildLocal && !loadLocal)
            {
                message = "BuildPath for group '" + assetGroup.Name + "' is set to the dynamic-lookup version of StreamingAssets, but LoadPath is not. \n";
            }
            else if (!buildLocal && loadLocal)
            {
                message = "LoadPath for group " + assetGroup.Name +
                          " is set to the dynamic-lookup version of StreamingAssets, but BuildPath is not. These paths must both use the dynamic-lookup, or both not use it. \n";
            }

            if (!string.IsNullOrEmpty(message))
            {
                message += "BuildPath: '" + buildPath + "'\n";
                message += "LoadPath: '" + loadPath + "'";
            }

            if (schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.LZMA && (buildLocal || loadLocal))
            {
                Debug.LogWarningFormat("Bundle compression is set to LZMA, but group {0} uses local content.", assetGroup.Name);
            }

            return message;
        }

        internal static string CalculateGroupHash(BundledAssetGroupSchema.BundleInternalIdMode mode, AddressableAssetGroup assetGroup, IEnumerable<AddressableAssetEntry> entries)
        {
            switch (mode)
            {
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuid: return assetGroup.Guid;
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdHash: return HashingMethods.Calculate(assetGroup.Guid, Application.cloudProjectId).ToString();
                case BundledAssetGroupSchema.BundleInternalIdMode.GroupGuidProjectIdEntriesHash:
                    return HashingMethods.Calculate(assetGroup.Guid, Application.cloudProjectId, new HashSet<string>(entries.Select(e => e.guid))).ToString();
            }

            throw new Exception("Invalid naming mode.");
        }

        /// <summary>
        /// Processes an AddressableAssetGroup and generates AssetBundle input definitions based on the BundlePackingMode.
        /// </summary>
        /// <param name="assetGroup">The AddressableAssetGroup to be processed.</param>
        /// <param name="bundleInputDefs">The list of bundle definitions fed into the build pipeline AssetBundleBuild</param>
        /// <param name="schema">The BundledAssetGroupSchema of used to process the assetGroup.</param>
        /// <param name="entryFilter">A filter to remove AddressableAssetEntries from being processed in the build.</param>
        /// <returns>The total list of AddressableAssetEntries that were processed.</returns>
        public static List<AddressableAssetEntry> PrepGroupBundlePacking(AddressableAssetGroup assetGroup, List<AssetBundleBuild> bundleInputDefs, BundledAssetGroupSchema schema,
            Func<AddressableAssetEntry, bool> entryFilter = null)
        {
            var combinedEntries = new List<AddressableAssetEntry>();
            var packingMode = schema.BundleMode;
            var namingMode = schema.InternalBundleIdMode;
            bool ignoreUnsupportedFilesInBuild = assetGroup.Settings.IgnoreUnsupportedFilesInBuild;

            switch (packingMode)
            {
                case BundledAssetGroupSchema.BundlePackingMode.PackTogether:
                    {
                        var allEntries = new List<AddressableAssetEntry>();
                        foreach (AddressableAssetEntry a in assetGroup.entries)
                        {
                            if (entryFilter != null && !entryFilter(a))
                                continue;
                            a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                        }

                        combinedEntries.AddRange(allEntries);
                        GenerateBuildInputDefinitions(allEntries, bundleInputDefs, CalculateGroupHash(namingMode, assetGroup, allEntries), "all", ignoreUnsupportedFilesInBuild);
                    }
                    break;
                case BundledAssetGroupSchema.BundlePackingMode.PackSeparately:
                    {
                        foreach (AddressableAssetEntry a in assetGroup.entries)
                        {
                            if (entryFilter != null && !entryFilter(a))
                                continue;
                            var allEntries = new List<AddressableAssetEntry>();
                            a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                            combinedEntries.AddRange(allEntries);
                            GenerateBuildInputDefinitions(allEntries, bundleInputDefs, CalculateGroupHash(namingMode, assetGroup, allEntries), a.address, ignoreUnsupportedFilesInBuild);
                        }
                    }
                    break;
                case BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel:
                    {
                        var labelTable = new Dictionary<string, List<AddressableAssetEntry>>();
                        foreach (AddressableAssetEntry a in assetGroup.entries)
                        {
                            if (entryFilter != null && !entryFilter(a))
                                continue;
                            var sb = new StringBuilder();
                            foreach (var l in a.labels)
                                sb.Append(l);
                            var key = sb.ToString();
                            List<AddressableAssetEntry> entries;
                            if (!labelTable.TryGetValue(key, out entries))
                                labelTable.Add(key, entries = new List<AddressableAssetEntry>());
                            entries.Add(a);
                        }

                        foreach (var entryGroup in labelTable)
                        {
                            var allEntries = new List<AddressableAssetEntry>();
                            foreach (var a in entryGroup.Value)
                            {
                                if (entryFilter != null && !entryFilter(a))
                                    continue;
                                a.GatherAllAssets(allEntries, true, true, false, entryFilter);
                            }

                            combinedEntries.AddRange(allEntries);
                            GenerateBuildInputDefinitions(allEntries, bundleInputDefs, CalculateGroupHash(namingMode, assetGroup, allEntries), entryGroup.Key, ignoreUnsupportedFilesInBuild);
                        }
                    }
                    break;
                default:
                    throw new Exception("Unknown Packing Mode");
            }

            return combinedEntries;
        }

        internal static void GenerateBuildInputDefinitions(List<AddressableAssetEntry> allEntries, List<AssetBundleBuild> buildInputDefs, string groupGuid, string address,
            bool ignoreUnsupportedFilesInBuild)
        {
            var scenes = new List<AddressableAssetEntry>();
            var assets = new List<AddressableAssetEntry>();
            foreach (var e in allEntries)
            {
                ThrowExceptionIfInvalidFiletypeOrAddress(e, ignoreUnsupportedFilesInBuild);
                if (string.IsNullOrEmpty(e.AssetPath))
                    continue;
                if (e.IsScene)
                    scenes.Add(e);
                else
                    assets.Add(e);
            }

            if (assets.Count > 0)
                buildInputDefs.Add(GenerateBuildInputDefinition(assets, groupGuid + "_assets_" + address + ".bundle"));
            if (scenes.Count > 0)
                buildInputDefs.Add(GenerateBuildInputDefinition(scenes, groupGuid + "_scenes_" + address + ".bundle"));
        }

        private static void ThrowExceptionIfInvalidFiletypeOrAddress(AddressableAssetEntry entry, bool ignoreUnsupportedFilesInBuild)
        {
            if (entry.guid.Length > 0 && entry.address.Contains('[') && entry.address.Contains(']'))
                throw new Exception($"Address '{entry.address}' cannot contain '[ ]'.");
            if (entry.MainAssetType == typeof(DefaultAsset) && !AssetDatabase.IsValidFolder(entry.AssetPath))
            {
                if (ignoreUnsupportedFilesInBuild)
                    Debug.LogWarning($"Cannot recognize file type for entry located at '{entry.AssetPath}'. Asset location will be ignored.");
                else
                    throw new Exception($"Cannot recognize file type for entry located at '{entry.AssetPath}'. Asset import failed for using an unsupported file type.");
            }
        }

        internal static AssetBundleBuild GenerateBuildInputDefinition(List<AddressableAssetEntry> assets, string name)
        {
            var assetInternalIds = new HashSet<string>();
            var assetsInputDef = new AssetBundleBuild();
            assetsInputDef.assetBundleName = name.ToLower().Replace(" ", "").Replace('\\', '/').Replace("//", "/");
            assetsInputDef.assetNames = assets.Select(s => s.AssetPath).ToArray();
            assetsInputDef.addressableNames = assets.Select(s => s.GetAssetLoadPath(true, assetInternalIds)).ToArray();
            return assetsInputDef;
        }


        // Tests can set this flag to prevent player script compilation. This is the most expensive part of small builds
        // and isn't needed for most tests.
        internal static bool s_SkipCompilePlayerScripts = false;

        static IList<IBuildTask> RuntimeDataBuildTasks(string builtinShaderBundleName, string monoScriptBundleName)
        {
            var buildTasks = new List<IBuildTask>();

            // Setup
            buildTasks.Add(new SwitchToBuildPlatform());
            buildTasks.Add(new RebuildSpriteAtlasCache());

            // Player Scripts
            if (!s_SkipCompilePlayerScripts)
                buildTasks.Add(new BuildPlayerScripts());
            buildTasks.Add(new PostScriptsCallback());

            // Dependency
            buildTasks.Add(new CalculateSceneDependencyData());
            buildTasks.Add(new CalculateAssetDependencyData());
            buildTasks.Add(new AddHashToBundleNameTask());
            buildTasks.Add(new StripUnusedSpriteSources());
            buildTasks.Add(new CreateBuiltInShadersBundle(builtinShaderBundleName));
            if (!string.IsNullOrEmpty(monoScriptBundleName))
                buildTasks.Add(new CreateMonoScriptBundle(monoScriptBundleName));
            buildTasks.Add(new PostDependencyCallback());

            // Packing
            buildTasks.Add(new GenerateBundlePacking());
            buildTasks.Add(new UpdateBundleObjectLayout());
            buildTasks.Add(new GenerateBundleCommands());
            buildTasks.Add(new GenerateSubAssetPathMaps());
            buildTasks.Add(new GenerateBundleMaps());
            buildTasks.Add(new PostPackingCallback());

            // Writing
            buildTasks.Add(new WriteSerializedFiles());
            buildTasks.Add(new ArchiveAndCompressBundles());
            buildTasks.Add(new GenerateLocationListsTask());
            buildTasks.Add(new PostWritingCallback());

            return buildTasks;
        }

        static void MoveFileToDestinationWithTimestampIfDifferent(string srcPath, string destPath, IBuildLogger log)
        {
            if (srcPath == destPath)
                return;

            DateTime time = File.GetLastWriteTime(srcPath);
            DateTime destTime = File.Exists(destPath) ? File.GetLastWriteTime(destPath) : new DateTime();

            if (destTime == time)
                return;

            using (log.ScopedStep(LogLevel.Verbose, "Move File", $"{srcPath} -> {destPath}"))
            {
                var directory = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                else if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(srcPath, destPath);
            }
        }

        void PostProcessBundles(AddressableAssetGroup assetGroup, IBundleBuildResults buildResult, AddressablesPlayerBuildResult addrResult, FileRegistry registry,
            AddressableAssetsBuildContext aaContext, Dictionary<string, string> bundleRenameMap, List<Action> postCatalogUpdateCallbacks)
        {
            var schema = assetGroup.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
                return;

            var path = schema.BuildPath.GetValue(assetGroup.Settings);
            if (string.IsNullOrEmpty(path))
            {
                path = aaContext.Settings.profileSettings.EvaluateString(aaContext.Settings.activeProfileId, schema.BuildPath.Id);
                if (string.IsNullOrEmpty(path))
                    return;
            }

            List<string> builtBundleNames = aaContext.assetGroupToBundles[assetGroup];
            List<string> outputBundleNames = null;

            if (m_GroupToBundleNames.TryGetValue(assetGroup, out (string, string)[] bundleValues))
            {
                outputBundleNames = new List<string>(builtBundleNames.Count);
                for (int i = 0; i < builtBundleNames.Count; ++i)
                {
                    string outputName = null;
                    foreach ((string, string) bundleValue in bundleValues)
                    {
                        if (schema.BundleMode == BundledAssetGroupSchema.BundlePackingMode.PackSeparately)
                        {
                            if (builtBundleNames[i].StartsWith(bundleValue.Item1, StringComparison.Ordinal))
                                outputName = bundleValue.Item2;
                        }
                        else if (builtBundleNames[i].Equals(bundleValue.Item1, StringComparison.Ordinal))
                            outputName = bundleValue.Item2;

                        if (outputName != null)
                            break;
                    }
                    outputBundleNames.Add(string.IsNullOrEmpty(outputName) ? builtBundleNames[i] : outputName);
                }
            }
            else
            {
                outputBundleNames = new List<string>(builtBundleNames);
            }

            for (int i = 0; i < builtBundleNames.Count; ++i)
            {
                AddressablesPlayerBuildResult.BundleBuildResult bundleResultInfo = new AddressablesPlayerBuildResult.BundleBuildResult();
                bundleResultInfo.SourceAssetGroup = assetGroup;

                if (aaContext.PrimaryKeyToLocation.TryGetValue(builtBundleNames[i], out ContentCatalogDataEntry dataEntry))
                {
                    var info = buildResult.BundleInfos[builtBundleNames[i]];
                    bundleResultInfo.Crc = info.Crc;
                    bundleResultInfo.Hash = info.Hash.ToString();
                    var requestOptions = new AssetBundleRequestOptions
                    {
                        Crc = schema.UseAssetBundleCrc ? info.Crc : 0,
                        UseCrcForCachedBundle = schema.UseAssetBundleCrcForCachedBundles,
                        UseUnityWebRequestForLocalBundles = schema.UseUnityWebRequestForLocalBundles,
                        Hash = schema.UseAssetBundleCache ? info.Hash.ToString() : "",
                        ChunkedTransfer = schema.ChunkedTransfer,
                        RedirectLimit = schema.RedirectLimit,
                        RetryCount = schema.RetryCount,
                        Timeout = schema.Timeout,
                        BundleName = Path.GetFileNameWithoutExtension(info.FileName),
                        AssetLoadMode = schema.AssetLoadMode,
                        BundleSize = GetFileSize(info.FileName),
                        ClearOtherCachedVersionsWhenLoaded = schema.AssetBundledCacheClearBehavior == BundledAssetGroupSchema.CacheClearBehavior.ClearWhenWhenNewVersionLoaded
                    };
                    dataEntry.Data = requestOptions;
                    bundleResultInfo.InternalBundleName = requestOptions.BundleName;

                    if (assetGroup == assetGroup.Settings.DefaultGroup && info.Dependencies.Length == 0 && !string.IsNullOrEmpty(info.FileName) &&
                        (info.FileName.EndsWith("_unitybuiltinshaders.bundle", StringComparison.Ordinal) || info.FileName.EndsWith("_monoscripts.bundle", StringComparison.Ordinal)))
                    {
                        outputBundleNames[i] = ConstructAssetBundleName(null, schema, info, outputBundleNames[i]);
                    }
                    else
                    {
                        int extensionLength = Path.GetExtension(outputBundleNames[i]).Length;
                        string[] deconstructedBundleName = outputBundleNames[i].Substring(0, outputBundleNames[i].Length - extensionLength).Split('_');
                        string reconstructedBundleName = string.Join("_", deconstructedBundleName, 1, deconstructedBundleName.Length - 1) + ".bundle";
                        outputBundleNames[i] = ConstructAssetBundleName(assetGroup, schema, info, reconstructedBundleName);
                    }

                    dataEntry.InternalId = dataEntry.InternalId.Remove(dataEntry.InternalId.Length - builtBundleNames[i].Length) + outputBundleNames[i];
                    SetPrimaryKey(dataEntry, outputBundleNames[i], aaContext);

                    if (!m_BundleToInternalId.ContainsKey(builtBundleNames[i]))
                        m_BundleToInternalId.Add(builtBundleNames[i], dataEntry.InternalId);

                    if (dataEntry.InternalId.StartsWith("http:\\", StringComparison.Ordinal))
                        dataEntry.InternalId = dataEntry.InternalId.Replace("http:\\", "http://").Replace("\\", "/");
                    else if (dataEntry.InternalId.StartsWith("https:\\", StringComparison.Ordinal))
                        dataEntry.InternalId = dataEntry.InternalId.Replace("https:\\", "https://").Replace("\\", "/");
                }
                else
                {
                    Debug.LogWarningFormat("Unable to find ContentCatalogDataEntry for bundle {0}.", outputBundleNames[i]);
                }

                var targetPath = Path.Combine(path, outputBundleNames[i]);
                bundleResultInfo.FilePath = targetPath;
                var srcPath = Path.Combine(assetGroup.Settings.buildSettings.bundleBuildPath, builtBundleNames[i]);

                if (assetGroup.GetSchema<BundledAssetGroupSchema>()?.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
                    outputBundleNames[i] = StripHashFromBundleLocation(outputBundleNames[i]);

                bundleRenameMap.Add(builtBundleNames[i], outputBundleNames[i]);
                MoveFileToDestinationWithTimestampIfDifferent(srcPath, targetPath, m_Log);
                AddPostCatalogUpdatesInternal(assetGroup, postCatalogUpdateCallbacks, dataEntry, targetPath, registry);

                if (addrResult != null)
                    addrResult.AssetBundleBuildResults.Add(bundleResultInfo);

                registry.AddFile(targetPath);
            }
        }

        internal void AddPostCatalogUpdatesInternal(AddressableAssetGroup assetGroup, List<Action> postCatalogUpdates, ContentCatalogDataEntry dataEntry, string targetBundlePath,
            FileRegistry registry)
        {
            if (assetGroup.GetSchema<BundledAssetGroupSchema>()?.BundleNaming ==
                BundledAssetGroupSchema.BundleNamingStyle.NoHash)
            {
                postCatalogUpdates.Add(() =>
                {
                    //This is where we strip out the temporary hash for the final bundle location and filename
                    string bundlePathWithoutHash = StripHashFromBundleLocation(targetBundlePath);
                    if (File.Exists(targetBundlePath))
                    {
                        if (File.Exists(bundlePathWithoutHash))
                            File.Delete(bundlePathWithoutHash);
                        string destFolder = Path.GetDirectoryName(bundlePathWithoutHash);
                        if (!string.IsNullOrEmpty(destFolder) && !Directory.Exists(destFolder))
                            Directory.CreateDirectory(destFolder);

                        File.Move(targetBundlePath, bundlePathWithoutHash);
                    }

                    if (registry != null)
                    {
                        if (!registry.ReplaceBundleEntry(targetBundlePath, bundlePathWithoutHash))
                            Debug.LogErrorFormat("Unable to find registered file for bundle {0}.", targetBundlePath);
                    }

                    if (dataEntry != null)
                        if (DataEntryDiffersFromBundleFilename(dataEntry, bundlePathWithoutHash))
                            dataEntry.InternalId = StripHashFromBundleLocation(dataEntry.InternalId);
                });
            }
        }

        // if false, there is no need to remove the hash from dataEntry.InternalId
        bool DataEntryDiffersFromBundleFilename(ContentCatalogDataEntry dataEntry, string bundlePathWithoutHash)
        {
            string dataEntryId = dataEntry.InternalId;
            string dataEntryFilename = Path.GetFileName(dataEntryId);
            string bundleFileName = Path.GetFileName(bundlePathWithoutHash);

            return dataEntryFilename != bundleFileName;
        }

        /// <summary>
        /// Creates a name for an asset bundle using the provided information.
        /// </summary>
        /// <param name="assetGroup">The asset group.</param>
        /// <param name="schema">The schema of the group.</param>
        /// <param name="info">The bundle information.</param>
        /// <param name="assetBundleName">The base name of the asset bundle.</param>
        /// <returns>Returns the asset bundle name with the provided information.</returns>
        protected virtual string ConstructAssetBundleName(AddressableAssetGroup assetGroup, BundledAssetGroupSchema schema, BundleDetails info, string assetBundleName)
        {
            if (assetGroup != null)
            {
                string groupName = assetGroup.Name.Replace(" ", "").Replace('\\', '/').Replace("//", "/").ToLower();
                assetBundleName = groupName + "_" + assetBundleName;
            }

            string bundleNameWithHashing = BuildUtility.GetNameWithHashNaming(schema.BundleNaming, info.Hash.ToString(), assetBundleName);
            //For no hash, we need the hash temporarily for content update purposes.  This will be stripped later on.
            if (schema.BundleNaming == BundledAssetGroupSchema.BundleNamingStyle.NoHash)
            {
                bundleNameWithHashing = bundleNameWithHashing.Replace(".bundle", "_" + info.Hash.ToString() + ".bundle");
            }

            return bundleNameWithHashing;
        }

        /// <summary>
        /// Sets the primary key of the given location. Syncing with other locations that have a dependency on this location
        /// </summary>
        /// <param name="forLocation">CatalogEntry to set the primary key for</param>
        /// <param name="newPrimaryKey">New Primary key to set on location</param>
        /// <param name="aaContext">Addressables build context to collect and assign other location data</param>
        /// <exception cref="ArgumentException"></exception>
        private void SetPrimaryKey(ContentCatalogDataEntry forLocation, string newPrimaryKey, AddressableAssetsBuildContext aaContext)
        {
            if (forLocation == null || forLocation.Keys == null || forLocation.Keys.Count == 0)
                throw new ArgumentException("Cannot change primary key. Invalid catalog entry");

            string originalKey = forLocation.Keys[0] as string;
            if (string.IsNullOrEmpty(originalKey))
                throw new ArgumentException("Invalid primary key for catalog entry " + forLocation.ToString());

            forLocation.Keys[0] = newPrimaryKey;
            aaContext.PrimaryKeyToLocation.Remove(originalKey);
            aaContext.PrimaryKeyToLocation.Add(newPrimaryKey, forLocation);

            if (!aaContext.PrimaryKeyToDependerLocations.TryGetValue(originalKey, out var dependers))
                return; // nothing depends on it

            foreach (ContentCatalogDataEntry location in dependers)
            {
                for (int i = 0; i < location.Dependencies.Count; ++i)
                {
                    string keyString = location.Dependencies[i] as string;
                    if (string.IsNullOrEmpty(keyString))
                        continue;
                    if (keyString == originalKey)
                    {
                        location.Dependencies[i] = newPrimaryKey;
                        break;
                    }
                }
            }

            aaContext.PrimaryKeyToDependerLocations.Remove(originalKey);
            aaContext.PrimaryKeyToDependerLocations.Add(newPrimaryKey, dependers);
        }

        private static long GetFileSize(string fileName)
        {
            try
            {
                return new FileInfo(fileName).Length;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return 0;
            }
        }

        /// <inheritdoc />
        public override void ClearCachedData()
        {
            if (Directory.Exists(Addressables.BuildPath))
            {
                try
                {
#if ENABLE_BINARY_CATALOG
                    var catalogPath = Addressables.BuildPath + "/catalog.bin";
                    DeleteFile(catalogPath);
#else
                    var catalogPath = Addressables.BuildPath + "/catalog.json";
                    DeleteFile(catalogPath);
#endif
                    var settingsPath = Addressables.BuildPath + "/settings.json";
                    DeleteFile(settingsPath);
                    Directory.Delete(Addressables.BuildPath, true);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <inheritdoc />
        public override bool IsDataBuilt()
        {
            var settingsPath = Addressables.BuildPath + "/settings.json";
            return !String.IsNullOrEmpty(m_CatalogBuildPath) &&
                File.Exists(m_CatalogBuildPath) &&
                File.Exists(settingsPath);
        }
    }
}
