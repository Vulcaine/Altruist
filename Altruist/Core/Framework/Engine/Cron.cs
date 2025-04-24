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

public static class CronPresets
{
    // Runs once every minute
    public const string EveryMinute = "* * * * *";

    // Runs at the start of every hour
    public const string Hourly = "0 * * * *";

    // Runs once a day at midnight
    public const string Daily = "0 0 * * *";

    // Runs once a week on Sunday at midnight
    public const string Weekly = "0 0 * * SUN";

    // Runs once a month on the first day at midnight
    public const string Monthly = "0 0 1 * *";

    // Runs once a year on January 1st at midnight
    public const string Yearly = "0 0 1 1 *";

    // Runs every 5 minutes
    public const string Every5Minutes = "*/5 * * * *";

    // Runs every 10 minutes
    public const string Every10Minutes = "*/10 * * * *";

    // Runs every 30 minutes
    public const string Every30Minutes = "*/30 * * * *";

    // Runs at 9 AM every weekday (Monday through Friday)
    public const string WeekdayMorning = "0 9 * * MON-FRI";

    // Runs at 6 PM every day
    public const string DailyEvening = "0 18 * * *";

    // Runs at 12 PM every Sunday
    public const string SundayNoon = "0 12 * * SUN";

    // Runs every hour on the hour between 8 AM and 6 PM, Monday through Friday
    public const string WorkdayHours = "0 8-18 * * MON-FRI";

    // Runs at midnight every 15th day of the month
    public const string Monthly15th = "0 0 15 * *";

    // Runs at 10:30 AM every Monday
    public const string Monday1030AM = "30 10 * * MON";

    // Runs every 15 minutes, on the hour, 15, 30, and 45
    public const string Every15Minutes = "0,15,30,45 * * * *";
}

public static class CronMapper
{
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