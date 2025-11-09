// // Gaming/Features/GameEngineFeatureProvider.cs
// using Altruist.Features;
// using Altruist.Gaming.Engine;
// using Microsoft.Extensions.DependencyInjection;

// namespace Altruist.Gaming.Features
// {
//     public sealed class GameEngineFeatureProvider : IAltruistFeatureProvider
//     {
//         public string FeatureId => "game-engine";


//         public object Configure(object stage, IServiceProvider services)
//         {
//             var root = services.GetRequiredService<AltruistConfigOptions>();

//             var game = root.Game;
//             // If no game section or empty, do nothing (Boot only requests this when present, but be defensive)
//             if (game == null || ((game.Worlds == null || game.Worlds.Items.Count == 0) && game.Engine == null))
//                 return stage;

//             if (stage is not AltruistIntermediateBuilder intermediate)
//             {
//                 throw new InvalidOperationException(
//                     $"Game engine feature expected stage AltruistIntermediateBuilder, but got {stage.GetType().Name}.");
//             }

//             // Build engine via your existing extension API
//             var next = intermediate.SetupGameEngine(engine =>
//             {
//                 var dim = game.Engine?.Dimension ?? "2D";
//                 foreach (var w in game.Worlds.Items)
//                 {
//                     if (dim == "3D")
//                         engine.AddWorld(new WorldIndex3D(w.Index, w.Size.ToVector3(), w.Gravity.ToVector3(), w.Position.ToVector3()));
//                     else
//                         engine.AddWorld(new WorldIndex2D(w.Index, w.Size.ToVector2(), w.Gravity.ToVector2(), w.Position.ToVector2()));
//                 }

//                 var hz = Math.Max(1, game.Engine?.FramerateHz ?? 60);
//                 var unit = ParseUnit(game.Engine?.Unit ?? "Ticks");
//                 var throttle = game.Engine?.Throttle;

//                 return engine.EnableEngine(
//                     hz: hz,
//                     unit: unit,
//                     throttle: throttle,
//                     mode: dim == "3D" ? EngineMode.ThreeD : EngineMode.TwoD
//                 );
//             });

//             return next;
//         }

//         private static CycleUnit ParseUnit(string value)
//             => value.Trim().ToLowerInvariant() == "seconds" ? CycleUnit.Seconds : CycleUnit.Ticks;
//     }
// }
