/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;
using Altruist.Gaming.TwoD;

namespace Altruist.Gaming
{
    /// <summary>
    /// Reports the persistent worlds of the 2D/3D world organizer to the <see cref="IServerNode"/>
    /// (kind <c>worlds</c>). Worlds are long-lived and count as units only; their load is
    /// <c>altruist:server:capacity:world-load</c> each (0 by default: worlds do not use up the budget
    /// that rooms are admitted against).
    /// </summary>
    [Service(typeof(ICapacityContributor))]
    [ConditionalOnConfig("altruist:game")]
    public sealed class WorldCapacityContributor : ICapacityContributor
    {
        private readonly Lazy<IGameWorldOrganizer2D?> _worlds2D;
        private readonly Lazy<IGameWorldOrganizer3D?> _worlds3D;
        private readonly double _loadPerWorld;

        public WorldCapacityContributor(
            Lazy<IGameWorldOrganizer2D?> worlds2D,
            Lazy<IGameWorldOrganizer3D?> worlds3D,
            [AppConfigValue("altruist:server:capacity:world-load", "0")] double loadPerWorld = 0)
        {
            _worlds2D = worlds2D;
            _worlds3D = worlds3D;
            _loadPerWorld = loadPerWorld;
        }

        public string Kind => "worlds";

        public CapacitySample Sample()
        {
            // The organizers return the dictionary's value collection: Count() reads its count without enumerating.
            var count = Count(() => _worlds2D.Value?.GetAllWorlds().Count() ?? 0)
                + Count(() => _worlds3D.Value?.GetAllWorlds().Count() ?? 0);
            return new CapacitySample(count, count * _loadPerWorld);
        }

        private static int Count(Func<int> count)
        {
            try
            { return count(); }
            catch (InvalidOperationException) { return 0; } // no organizer registered in this mode
        }
    }
}
