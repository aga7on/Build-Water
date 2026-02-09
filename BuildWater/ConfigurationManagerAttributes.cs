using System;
using BepInEx.Configuration;

namespace BuildWater
{
	internal sealed class ConfigurationManagerAttributes
	{
		public bool? ReadOnly;
		public bool? ShowRangeAsPercent;
		public int? Order;
		public bool? IsAdvanced;
		public string Category;
		public string Subcategory;
		public Action<ConfigEntryBase> CustomDrawer;
		public bool? Browsable;
		public bool? HideSettingName;
	}
}
