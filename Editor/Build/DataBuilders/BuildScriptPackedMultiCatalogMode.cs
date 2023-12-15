using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline.Utilities;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
	/// <summary>
	/// Build script used for player builds and running with bundles in the editor, allowing building of multiple catalogs.
	/// </summary>
	[CreateAssetMenu(fileName = "BuildScriptPackedMultiCatalog.asset", menuName = "Addressables/Content Builders/Multi-Catalog Build Script")]
	public class BuildScriptPackedMultiCatalogMode : BuildScriptPackedMode, IMultipleCatalogsBuilder
	{
		public static BuildScriptPackedMultiCatalogMode buildScriptPackedMulti;

		/// <summary>
		/// Move the bundles related to a specific ExternalCatalog to a separate folder.
		/// </summary>
        private void BundleMover()
        {
			foreach (var setup in catalogSetups)
            {
				if (setup.IsEmpty)
				{
					continue;
				}				

				foreach (var loc in setup.catalogBundles)
                {
					var bundleName = Path.GetFileName(loc.InternalId);
					string[] bundleNameArray = bundleName.Split("_assets_all_", StringSplitOptions.RemoveEmptyEntries);
					var bundleName_prefix = bundleNameArray[0];
					foreach (ExternalCatalogSetup externalCatalog in externalCatalogs)
					{
						string externalCatalogName = externalCatalog.CatalogName;
						var externalCatalogFilesFolder = remoteBuildFolder_global + "/" + externalCatalogName;
						foreach (AddressableAssetGroup addressableAssetGroup in externalCatalog.AssetGroups)
						{
							string groupName = addressableAssetGroup.Name;
							if (bundleName_prefix.Equals(groupName))
							{								
								FileIntegration(remoteBuildFolder_global, externalCatalogFilesFolder, bundleName);
							}
						}
					}
				}
			}			
		}

		/// <summary>
		/// Move the json related to a specific ExternalCatalog to a separate folder.
		/// </summary>
		private void JsonMover()
		{
			foreach (ExternalCatalogSetup externalCatalog in externalCatalogs)
			{
				string externalCatalogName = externalCatalog.CatalogName;
				var externalCatalogFilesFolder = remoteBuildFolder_global + "/" + externalCatalogName;
				var jsonName = externalCatalogName + ".json";
				FileIntegration(remoteBuildFolder_global, externalCatalogFilesFolder, jsonName);
			}
		}

		/// <summary>
		/// Move the file with fileName to a separate folder
		/// </summary>
		private static void FileIntegration(string src, string dst, string fileName)
		{
			if (src == dst)
			{
				return;
			}

			if (!Directory.Exists(dst))
            {
				Directory.CreateDirectory(dst);
			}

			if (File.Exists(dst + "/" + fileName))
            {
				File.Delete(dst + "/" + fileName);
            }

			if (!Directory.Exists(src))
			{
				Debug.LogError("Build Path Folder is not exist. Check your catalog build path setting.");
			}
            else
            {
				File.Move(src + "/" + fileName, dst + "/" + fileName);				
			}
		}

		/// <summary>
		/// Move a file, deleting it first if it exists.
		/// </summary>
		private static void FileMoveOverwrite(string src, string dst)
		{
			if (src == dst)
			{
				return;
			}

			if (File.Exists(dst))
			{
				File.Delete(dst);
			}

			File.Move(src, dst);
		}

		[SerializeField]
		private List<ExternalCatalogSetup> externalCatalogs = new List<ExternalCatalogSetup>();

		private readonly List<CatalogSetup> catalogSetups = new List<CatalogSetup>();

		public override string Name
		{
			get => base.Name + " - Multi-Catalog";
		}

		public List<ExternalCatalogSetup> ExternalCatalogs
		{
			get => externalCatalogs;
			set => externalCatalogs = value;
		}

		protected override List<ContentCatalogBuildInfo> GetContentCatalogs(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
		{
			// cleanup
			catalogSetups.Clear();

			// Prepare catalogs
			var defaultCatalog = base.GetContentCatalogs(builderInput, aaContext).First();
			var allCatalogs = base.GetContentCatalogs(builderInput, aaContext);
			defaultCatalog.Locations.Clear(); // This will get filled up again below, but filtered by external catalog setups.
			foreach (ExternalCatalogSetup catalogContentGroup in externalCatalogs)
			{
				if (catalogContentGroup != null)
				{
					catalogSetups.Add(new CatalogSetup(catalogContentGroup, builderInput, aaContext));
				}
			}

			// Assign assets to new catalogs based on included groups
			var profileSettings = aaContext.Settings.profileSettings;
			var profileId = aaContext.Settings.activeProfileId;
			// kLocalLoadPath = "Local.LoadPath"
			var defaultLoadPathData = profileSettings.GetProfileDataByName(AddressableAssetSettings.kLocalLoadPath);

			foreach (var loc in aaContext.locations)
			{
				var preferredCatalog = catalogSetups.FirstOrDefault(cs => cs.CatalogContentGroup.IsPartOfCatalog(loc, aaContext));
				if (preferredCatalog != null)
				{
					if (loc.ResourceType == typeof(IAssetBundleResource))
					{
						var bundleId = (loc.Data as AssetBundleRequestOptions).BundleName + ".bundle";
						var group = aaContext.Settings.FindGroup(g => (g != null) && (g.Guid == aaContext.bundleToAssetGroup[bundleId]));

						if (group == null)
						{
							Debug.LogErrorFormat("Could not find the group that belongs to location {0}.", loc.InternalId);
							continue;
						}

						// If the group's load path is set to be the global local load path, then it is switched out for the one defined by the external catalog.
						var schema = group.GetSchema<BundledAssetGroupSchema>();
						var loadPath =
							(schema.LoadPath.Id == defaultLoadPathData?.Id) ?
							preferredCatalog.CatalogContentGroup.RuntimeLoadPath :
							schema.LoadPath;

						// Generate a new load path based on the settings of the external catalog or the schema's custom defined values.						
						var filename = GenerateLocationListsTask.GetFileName(loc.InternalId, builderInput.Target);						
						var runtimeLoadPath = GenerateLocationListsTask.GetLoadPath(group, loadPath, filename, builderInput.Target);

						preferredCatalog.BuildInfo.Locations.Add(new ContentCatalogDataEntry(typeof(IAssetBundleResource), runtimeLoadPath, loc.Provider, loc.Keys, loc.Dependencies, loc.Data));
						preferredCatalog.catalogBundles.Add(loc);
					}
					else
					{
						preferredCatalog.BuildInfo.Locations.Add(loc);
					}
				}
				else
				{
					defaultCatalog.Locations.Add(loc);
				}
			}

			foreach (CatalogSetup additionalCatalog in catalogSetups)
			{
				var locationQueue = new Queue<ContentCatalogDataEntry>(additionalCatalog.BuildInfo.Locations);
				var processedLocations = new HashSet<ContentCatalogDataEntry>();

                while (locationQueue.Count > 0)
                {
                    ContentCatalogDataEntry location = locationQueue.Dequeue();

                    // If the location has already been processed, or doesn't have any dependencies, then skip it.
                    if (!processedLocations.Add(location) || (location.Dependencies == null) || (location.Dependencies.Count == 0))
                    {
                        continue;
                    }

                    foreach (var entryDependency in location.Dependencies)
                    {
                        // Search for the dependencies in the default catalog only.
                        var depLocation = defaultCatalog.Locations.Find(loc => loc.Keys[0] == entryDependency);

                        if (depLocation != null)
                        {
                            locationQueue.Enqueue(depLocation);

                            // If the dependency wasn't part of the catalog yet, add it.
                            if (!additionalCatalog.BuildInfo.Locations.Contains(depLocation))
                            {
                                additionalCatalog.BuildInfo.Locations.Add(depLocation);
                            }
                        }
                        else if (!additionalCatalog.BuildInfo.Locations.Exists(loc => loc.Keys[0] == entryDependency))
                        {
                            Debug.LogErrorFormat("Could not find location for dependency ID {0} in the default catalog.", entryDependency);
                        }
                    }
                }
            }

			// Gather catalogs (with out defaultCatalog)
			var catalogs = new List<ContentCatalogBuildInfo>(catalogSetups.Count + 1);

			// We don't need defaultCatalog
			//catalogs.Add(defaultCatalog);

			foreach (var setup in catalogSetups)
			{
				if (!setup.IsEmpty)
				{
					catalogs.Add(setup.BuildInfo);
				}
			}

			return catalogs;
		}

		protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
		{
			// Get the instantiate of this class
			BuildScriptPackedMultiCatalogMode.buildScriptPackedMulti = this;

			// execute build script
			var result = base.DoBuild<TResult>(builderInput, aaContext);

			// Move out the extra catalogs and their content.
			foreach (var setup in catalogSetups)
			{
				// Empty catalog setups are not added/built.
				if (setup.IsEmpty)
				{
					continue;
				}

				var profileSettings = aaContext.Settings.profileSettings;
				var activeProfileId = aaContext.Settings.activeProfileId;
				var defaultBuildPathData = profileSettings.GetProfileDataByName(AddressableAssetSettings.kLocalBuildPath);
				var globalBuildPath = profileSettings.EvaluateString(activeProfileId, profileSettings.GetValueById(activeProfileId, defaultBuildPathData.Id));

				foreach (var loc in setup.catalogBundles)
				{
					var bundleId = (loc.Data as AssetBundleRequestOptions).BundleName + ".bundle";
					var group = aaContext.Settings.FindGroup(g => (g != null) && (g.Guid == aaContext.bundleToAssetGroup[bundleId]));
					var schema = group.GetSchema<BundledAssetGroupSchema>();

					// If the schema defines a different build path than the default catalog's build path,
					// then the user made a conscious action to place the bundle somewhere and nothing else has
					// to be done anymore. If it's still pointing towards the default catalog's build path,
					// then it should be moved out.
					if (schema.BuildPath.Id != defaultBuildPathData.Id)
					{
						continue;
					}

					// Move the bundle out of the default build location.
					var bundleName = Path.GetFileName(loc.InternalId);
					var bundleSrcPath = Path.Combine(globalBuildPath, bundleName);
					var bundleDstPath = Path.Combine(setup.BuildInfo.BuildPath, bundleName);
					FileMoveOverwrite(bundleSrcPath, bundleDstPath);
				}
			}

			// Move out the extra catalog jsons.
			JsonMover();
			// Move out the extra catalog bundles.
			BundleMover();

			return result;
		}

		public override void ClearCachedData()
		{
			base.ClearCachedData();

			if ((externalCatalogs == null) || (externalCatalogs.Count == 0))
			{
				return;
			}

			// Cleanup the additional catalogs
			var profileSettings = AddressableAssetSettingsDefaultObject.Settings.profileSettings;
			var profileId = AddressableAssetSettingsDefaultObject.Settings.activeProfileId;

			var libraryDirectory = new DirectoryInfo("Library");
			var assetsDirectory = new DirectoryInfo("Assets");

			foreach (ExternalCatalogSetup externalCatalog in externalCatalogs)
			{
				string buildPath = externalCatalog.BuildPath.GetValue(profileSettings, profileId);
				if (string.IsNullOrEmpty(buildPath))
				{
					buildPath = externalCatalog.BuildPath.Id;
				}

				if (!Directory.Exists(buildPath))
				{
					continue;
				}

				// Stop if we're about to delete the whole library or assets directory.
				var buildDirectory = new DirectoryInfo(buildPath);
				if ((Path.GetRelativePath(buildDirectory.FullName, libraryDirectory.FullName) == ".") ||
					(Path.GetRelativePath(buildDirectory.FullName, assetsDirectory.FullName) == "."))
				{
					continue;
				}

				// Delete each file in the build directory.
				foreach (string catalogFile in Directory.GetFiles(buildPath))
				{
					File.Delete(catalogFile);
				}

				Directory.Delete(buildPath, true);
			}
		}

		private class CatalogSetup
		{
			public readonly ExternalCatalogSetup CatalogContentGroup = null;

			/// <summary>
			/// The catalog build info.
			/// </summary>
			public readonly ContentCatalogBuildInfo BuildInfo = null;

			/// <summary>
			/// Tells whether the catalog is empty.
			/// </summary>
			public bool IsEmpty => BuildInfo.Locations.Count == 0;

			/// <summary>
			/// The list of bundles that are associated with this catalog setup.
			/// </summary>
			public readonly List<ContentCatalogDataEntry> catalogBundles = new List<ContentCatalogDataEntry>();

			public CatalogSetup(ExternalCatalogSetup buildCatalog, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
			{
				this.CatalogContentGroup = buildCatalog;

				var profileSettings = aaContext.Settings.profileSettings;
				var profileId = aaContext.Settings.activeProfileId;
				// builderInput.RuntimeCatalogFilename="catalog.json", Path.GetExtension(builderInput.RuntimeCatalogFilename)=".json"
				// buildCatalog.CatalogName is the Catalog Name defined by user in ExternalCatalogs folder
				var catalogFileName = $"{buildCatalog.CatalogName}{Path.GetExtension(builderInput.RuntimeCatalogFilename)}";
				
				BuildInfo = new ContentCatalogBuildInfo(buildCatalog.CatalogName, catalogFileName);

				// Set the build path.
				BuildInfo.BuildPath = buildCatalog.BuildPath.GetValue(profileSettings, profileId);
				if (string.IsNullOrEmpty(BuildInfo.BuildPath))
				{
					BuildInfo.BuildPath = profileSettings.EvaluateString(profileId, buildCatalog.BuildPath.Id);

					if (string.IsNullOrWhiteSpace(BuildInfo.BuildPath))
					{
						throw new Exception($"The catalog build path for external catalog '{buildCatalog.name}' is empty.");
					}
				}

				// Set the load path.
				BuildInfo.LoadPath = buildCatalog.RuntimeLoadPath.GetValue(profileSettings, profileId);
				if (string.IsNullOrEmpty(BuildInfo.LoadPath))
				{
					BuildInfo.LoadPath = profileSettings.EvaluateString(profileId, buildCatalog.RuntimeLoadPath.Id);
				}

				BuildInfo.Register = false;
			}
		}

		// Used to store Remote Build Path.
		public string remoteBuildFolder_global = "";

		public override string[] CreateRemoteHash(string jsonText, List<ResourceLocationData> locations, AddressableAssetSettings aaSettings, AddressablesDataBuilderInput builderInput, ProviderLoadRequestOptions catalogLoadOptions, string contentHash, string catalogName)
        {
            string[] dependencyHashes = null;

			//BuildScriptPackedMultiCatalogMode.buildScriptPackedMulti = this;

			Debug.LogWarning("BuildScriptPackedMultiCatalogMode.CreateMultiHashes : " + BuildScriptPackedMultiCatalogMode.buildScriptPackedMulti);

			if (string.IsNullOrEmpty(contentHash))
				contentHash = HashingMethods.Calculate(jsonText).ToString();

			string externalCatalogName = catalogName;
			
			string contentHash_external = contentHash;

			// Use external catalog name as hash file name.
			var externalHashName = aaSettings.profileSettings.EvaluateString(aaSettings.activeProfileId, externalCatalogName);
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
				var remoteHashBuildPath = remoteBuildFolder + "/" + externalHashName + ".hash";
				WriteFile(remoteHashBuildPath, contentHash_external, builderInput.Registry);

				dependencyHashes = new string[((int)ContentCatalogProvider.DependencyHashIndex.Count)];
				dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] = ResourceManagerRuntimeData.kCatalogAddress + "RemoteHash";
				dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] = ResourceManagerRuntimeData.kCatalogAddress + "CacheHash";

				var remoteHashLoadPath = remoteLoadFolder + "/" + externalHashName + ".hash";
				var remoteHashLoadLocation = new ResourceLocationData(
					new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Remote] },
					remoteHashLoadPath,
					typeof(TextDataProvider), typeof(string));
				remoteHashLoadLocation.Data = catalogLoadOptions.Copy();
				locations.Add(remoteHashLoadLocation);

#if UNITY_SWITCH
                var cacheLoadPath = remoteHashLoadPath; // ResourceLocationBase does not allow empty string id
#else
				var cacheLoadPath = "{UnityEngine.Application.persistentDataPath}/com.unity.addressables" + "/" + externalHashName + ".hash";
#endif
				var cacheLoadLocation = new ResourceLocationData(
					new[] { dependencyHashes[(int)ContentCatalogProvider.DependencyHashIndex.Cache] },
					cacheLoadPath,
					typeof(TextDataProvider), typeof(string));
				cacheLoadLocation.Data = catalogLoadOptions.Copy();
				locations.Add(cacheLoadLocation);

				// Move out the extra catalog hashes.
				var externalCatalogFilesFolder = remoteBuildFolder + "/" + externalCatalogName;
				var hashName = externalCatalogName + ".hash";
				FileIntegration(remoteBuildFolder, externalCatalogFilesFolder, hashName);

				remoteBuildFolder_global = remoteBuildFolder;
			}

            return dependencyHashes;
        }
    }
}
