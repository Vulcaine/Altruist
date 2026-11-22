/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

/// <summary>
/// Ready-made 5-field cron expressions (minute hour day-of-month month day-of-week) for
/// <c>[Cycle("...")]</c> and <c>IEngineCore.RegisterCronJob</c>. Times are evaluated in UTC.
/// Any other valid 5-field cron string works too; these are just named shortcuts.
/// </summary>
/// <example>
/// <code>
/// [Cycle(CronPresets.Every5Minutes)]
/// public Task PruneAsync() { ... }
/// </code>
/// </example>
public static class CronPresets
{
    /// <summary>Runs once every minute.</summary>
    public const string EveryMinute = "* * * * *";

    /// <summary>Runs at the start of every hour.</summary>
    public const string Hourly = "0 * * * *";

    /// <summary>Runs once a day at midnight.</summary>
    public const string Daily = "0 0 * * *";

    /// <summary>Runs once a week on Sunday at midnight.</summary>
    public const string Weekly = "0 0 * * SUN";

    /// <summary>Runs once a month on the first day at midnight.</summary>
    public const string Monthly = "0 0 1 * *";

    /// <summary>Runs once a year on January 1st at midnight.</summary>
    public const string Yearly = "0 0 1 1 *";

    /// <summary>Runs every 5 minutes.</summary>
    public const string Every5Minutes = "*/5 * * * *";

    /// <summary>Runs every 10 minutes.</summary>
    public const string Every10Minutes = "*/10 * * * *";

    /// <summary>Runs every 30 minutes.</summary>
    public const string Every30Minutes = "*/30 * * * *";

    /// <summary>Runs at 9 AM every weekday (Monday through Friday).</summary>
    public const string WeekdayMorning = "0 9 * * MON-FRI";

    /// <summary>Runs at 6 PM every day.</summary>
    public const string DailyEvening = "0 18 * * *";

    /// <summary>Runs at 12 PM every Sunday.</summary>
    public const string SundayNoon = "0 12 * * SUN";

    /// <summary>Runs every hour on the hour between 8 AM and 6 PM, Monday through Friday.</summary>
    public const string WorkdayHours = "0 8-18 * * MON-FRI";

    /// <summary>Runs at midnight every 15th day of the month.</summary>
    public const string Monthly15th = "0 0 15 * *";

    /// <summary>Runs at 10:30 AM every Monday.</summary>
    public const string Monday1030AM = "30 10 * * MON";

    /// <summary>Runs every 15 minutes, on the hour, 15, 30, and 45.</summary>
    public const string Every15Minutes = "0,15,30,45 * * * *";
}

/// <summary>Turns the <see cref="CronPresets"/> expressions into human-readable text (for logs and dashboards).</summary>
public static class CronMapper
{
    /// <summary>
    /// Returns an English description for an expression that exactly matches one of the <see cref="CronPresets"/>
    /// constants; any other expression yields <c>"Unknown cron expression"</c> (it is not parsed).
    /// </summary>
    /// <param name="cronExpression">The cron expression.</param>
    /// <returns>The description, or <c>"Unknown cron expression"</c>.</returns>
    public static string MapCronToReadableFormat(string cronExpression)
    {
        return cronExpression switch
        {
            CronPresets.EveryMinute => "Every minute",
            CronPresets.Hourly => "At the start of every hour",
            CronPresets.Daily => "Once a day at midnight",
            CronPresets.Weekly => "Once a week on Sunday at midnight",
            CronPresets.Monthly => "Once a month on the first day at midnight",
            CronPresets.Yearly => "Once a year on January 1st at midnight",
            CronPresets.Every5Minutes => "Every 5 minutes",
            CronPresets.Every10Minutes => "Every 10 minutes",
            CronPresets.Every30Minutes => "Every 30 minutes",
            CronPresets.WeekdayMorning => "At 9 AM every weekday (Monday through Friday)",
            CronPresets.DailyEvening => "At 6 PM every day",
            CronPresets.SundayNoon => "At 12 PM every Sunday",
            CronPresets.WorkdayHours => "Every hour on the hour between 8 AM and 6 PM, Monday through Friday",
            CronPresets.Monthly15th => "At midnight every 15th day of the month",
            CronPresets.Monday1030AM => "At 10:30 AM every Monday",
            CronPresets.Every15Minutes => "Every 15 minutes, on the hour, 15, 30, and 45",
            _ => "Unknown cron expression"
        };
    }
}
