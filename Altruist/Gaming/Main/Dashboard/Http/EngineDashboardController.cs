/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Engine;
using Altruist.Gaming;

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    /// <summary>
    /// <c>/dashboard/v1/engine</c>: the game loop for operators: frame timing against the target rate, the world
    /// steppers with their clocks and timings, every <c>[Cycle]</c> task and cron job, and the failures by source.
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/engine")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class EngineDashboardController : ControllerBase
    {
        private readonly IEngineCore? _engine;
        private readonly IGameWorldOrganizer? _worlds;
        private readonly IStepScheduler? _scheduler;
        private readonly DashboardEngineMetrics? _metrics;

        /// <summary>Created by ASP.NET Core through DI.</summary>
        public EngineDashboardController(IEngineCore? engine = null, IGameWorldOrganizer? worlds = null, IStepScheduler? scheduler = null, DashboardEngineMetrics? metrics = null)
        {
            _engine = engine;
            _worlds = worlds;
            _scheduler = scheduler;
            _metrics = metrics;
        }

        /// <summary><c>GET /dashboard/v1/engine</c>: the report (<see cref="EngineReportDto"/>); <c>running</c> is false without an engine.</summary>
        [HttpGet]
        public ActionResult<EngineReportDto> Get()
        {
            var inspection = _engine as IEngineInspection;
            var coordinator = _worlds as WorldCoordinator;
            var timings = _metrics?.Read() ?? Array.Empty<StepperTimingDto>();
            var steppers = coordinator?.Steppers.Select(s =>
            {
                var clock = coordinator.ClockOf(s);
                var name = s.GetType().Name;
                return new StepperReportDto(
                    name,
                    s.Mode.ToString(),
                    clock?.Hz ?? s.FixedHz,
                    clock?.TotalSteps,
                    timings.FirstOrDefault(t => t.Name == name));
            }).ToList() ?? new List<StepperReportDto>();

            return Ok(new EngineReportDto(
                inspection is not null && inspection.Enabled,
                inspection?.Frame ?? 0,
                (_engine as AltruistEngine)?.WorldStep.ToString(),
                inspection?.Frames(),
                timings.FirstOrDefault(t => t.Name == "frame"),
                coordinator is null ? null : new CoordinatorReportDto(coordinator.Frame, coordinator.MaxFrameDelta, coordinator.MaxStepsPerFrame, coordinator.Overrun.ToString()),
                _scheduler?.Workers,
                steppers,
                inspection?.CycleTasks() ?? Array.Empty<EngineTaskReport>(),
                inspection?.CronJobs() ?? Array.Empty<CronJobReport>(),
                inspection?.Faults() ?? Array.Empty<EngineFaultReport>()));
        }
    }

    /// <summary>Response of <c>GET /dashboard/v1/engine</c>.</summary>
    public sealed record EngineReportDto(
        bool Running,
        long Frame,
        string? WorldStep,
        EngineFrameReport? Frames,
        StepperTimingDto? WorldFrame,
        CoordinatorReportDto? Coordinator,
        int? StepWorkers,
        IReadOnlyList<StepperReportDto> Steppers,
        IReadOnlyList<EngineTaskReport> CycleTasks,
        IReadOnlyList<CronJobReport> CronJobs,
        IReadOnlyList<EngineFaultReport> Faults);

    /// <summary>The world coordinator's settings and progress.</summary>
    public sealed record CoordinatorReportDto(long Frame, double MaxFrameDelta, int MaxStepsPerFrame, string Overrun);

    /// <summary>One world stepper: how it steps and how long it takes.</summary>
    public sealed record StepperReportDto(string Name, string Mode, int FixedHz, long? TotalSteps, StepperTimingDto? Timing);
}
