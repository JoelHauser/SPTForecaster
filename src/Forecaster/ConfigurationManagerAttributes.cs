namespace Forecaster
{
    /// <summary>
    /// Read by the F12 configuration manager (and BetterBepInEx, which uses its setting list) from a setting's
    /// tags, by type name and field name. Only the field this mod uses.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
    }
}
