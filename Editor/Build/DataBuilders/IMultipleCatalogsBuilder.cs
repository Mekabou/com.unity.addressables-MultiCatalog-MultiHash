using System.Collections.Generic;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
	public interface IMultipleCatalogsBuilder
	{
		List<ExternalCatalogSetup> ExternalCatalogs
		{
			get; set;
		}
	}
}
