namespace Altruist.Gaming
{

    [AltruistModule]
    public static class GamingExtension
    {

        [AltruistModuleLoader]
        public static async Task Load()
        {
            AltruistBootstrap.AddConfiguration(new PrefabConfig());
        }

    }
}