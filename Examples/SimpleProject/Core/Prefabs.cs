/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.UORM;

namespace SimpleGame.Entities;

[Vault("player")]
public class Spaceship : VaultModel
{
    [VaultColumn("speed")]
    public int Speed { get; set; }

}

[Prefab("Spaceship")]
public class SimpleSpaceshipPrefab : Prefab3D
{
    [PostConstruct]
    public void Init(IPrefabEditor3D editor)
    {
        var spaceship = editor.Edit(this).Add<Spaceship>();
        spaceship.Speed = 5;
    }
}
