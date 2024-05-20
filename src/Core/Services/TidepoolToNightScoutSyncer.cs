using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TidepoolToNightScoutSync.Core.Model.Nightscout;
using TidepoolToNightScoutSync.Core.Services.Nightscout;
using TidepoolToNightScoutSync.Core.Services.Tidepool;

namespace TidepoolToNightScoutSync.Core.Services;

public class TidepoolToNightScoutSyncer(
    ITidepoolClientFactory factory,
    NightscoutClient nightscout,
    IOptions<TidepoolToNightScoutSyncerOptions> options)
{
    private readonly TidepoolToNightScoutSyncerOptions _options = options.Value;
    private ITidepoolClient? _tidepool;

    public async Task<Profile?> SyncProfiles(DateTime? since = null, DateTime? till = null)
    {
        var nfi = new CultureInfo("en-US", false).NumberFormat;
        since ??= _options.Since ?? DateTime.Today;
        till ??= _options.Till;
        _tidepool ??= await factory.CreateAsync();

        var settings = await _tidepool.GetPumpSettingsAsync(since, till);
        var setting = settings.MaxBy(x => x.DeviceTime);
        if (setting == null) return null;

        var profile = new Profile
        {
            DefaultProfile = setting.ActiveSchedule,
            StartDate = setting.DeviceTime,
            Units = setting.Units.Bg,
            Mills = new DateTimeOffset(setting.DeviceTime ?? DateTime.UtcNow).ToUnixTimeMilliseconds().ToString()
        };

        // map basal schedules
        foreach (var (name, schedule) in setting.BasalSchedules.Select(x => (x.Key, x.Value)))
        {
            profile.Store.TryAdd(name, new ProfileInfo());
            profile.Store[name].Basal.AddRange(schedule.Select(x => new Basal
            {
                Time = TimeSpan.FromSeconds(x.Start / 1000).ToString(@"hh\:mm"),
                TimeAsSeconds = (x.Start / 1000).ToString(),
                Value = x.Rate.ToString(nfi)
            }));
        }

        // map bg targets            
        foreach (var (name, targets) in setting.BgTargets.Select(x => (x.Key, x.Value)))
        {
            profile.Store.TryAdd(name, new ProfileInfo());
            foreach (var target in targets)
            {
                // convert from target glucose value to target glucose interval
                // e.g. 6,66089758925464 -->  (3.7, 10.360897589254641)
                profile.Store[name].TargetLow.Add(new Target
                {
                    Time = TimeSpan.FromSeconds(target.Start / 1000).ToString(@"hh\:mm"),
                    TimeAsSeconds = (target.Start / 1000).ToString(),
                    Value = _options.TargetLow.ToString(nfi),
                });

                profile.Store[name].TargetHigh.Add(new Target
                {
                    Time = TimeSpan.FromSeconds(target.Start / 1000).ToString(@"hh\:mm"),
                    TimeAsSeconds = (target.Start / 1000).ToString(),
                    Value = (_options.TargetLow + target.Target).ToString(nfi),
                });
            }
        }

        // map carb ratios
        foreach (var (name, carbRatios) in setting.CarbRatios.Select(x => (x.Key, x.Value)))
        {
            profile.Store.TryAdd(name, new ProfileInfo());
            profile.Store[name].Carbratio.AddRange(carbRatios.Select(x => new Carbratio
            {
                Time = TimeSpan.FromSeconds(x.Start / 1000).ToString(@"hh\:mm"),
                TimeAsSeconds = (x.Start / 1000).ToString(),
                Value = x.Amount.ToString(nfi)
            }));
        }

        // map insulin sensitivities
        foreach (var (name, sensitivities) in setting.InsulinSensitivities.Select(x => (x.Key, x.Value)))
        {
            profile.Store.TryAdd(name, new ProfileInfo());
            profile.Store[name].Sens.AddRange(sensitivities.Select(x => new Sen
            {
                Time = TimeSpan.FromSeconds(x.Start / 1000).ToString(@"hh\:mm"),
                TimeAsSeconds = (x.Start / 1000).ToString(),
                Value = x.Amount.ToString(nfi)
            }));
        }

        // try to match on existing profile
        var profiles = await nightscout.GetProfiles();
        profile.Id = profiles.FirstOrDefault(x => x.Mills == profile.Mills)?.Id;

        return await nightscout.SetProfile(profile);
    }

    public async Task<IReadOnlyList<Treatment>> SyncAsync(DateTime? since = null, DateTime? till = null)
    {
        since ??= _options.Since ?? DateTime.Today;
        till ??= _options.Till;
        _tidepool ??= await factory.CreateAsync();

        var status = await nightscout.GetStatus();
        var nightScoutUnits = status.Settings?.Units ?? "mg/dl";

        var boluses = (await _tidepool.GetBolusAsync(since, till))
            .GroupBy(x => x.Time)
            .Select(x => x.First())
            .ToDictionary(x => x.Time, x => x);

        var food = (await _tidepool.GetFoodAsync(since, till))
            .GroupBy(x => x.Time)
            .Select(x => x.First())
            .ToDictionary(x => x.Time, x => x);

        var activity = await _tidepool.GetPhysicalActivityAsync(since, till);

        var bgValues = await _tidepool.GetBgValues(since, till);

        var treatments = new Dictionary<DateTime, Treatment>();

        // standalone boluses and boluses with food
        foreach (var bolus in boluses.Values)
        {
            if (!bolus.Time.HasValue)
            {
                continue;
            }

            if (!treatments.TryGetValue(bolus.Time.Value, out var treatment))
            {
                treatment = treatments[bolus.Time.Value] = new Treatment();
            }

            treatment.Carbs = food.GetValueOrDefault(bolus.Time)?.Nutrition?.Carbohydrate?.Net;
            treatment.Insulin = bolus.Normal;
            treatment.Duration = bolus.Duration?.TotalMinutes;
            treatment.Relative = bolus.Extended;
            treatment.CreatedAt = bolus.Time;
            treatment.EnteredBy = "Tidepool";
        }

        // food without boluses
        foreach (var item in food.Values)
        {
            if (!item.Time.HasValue)
            {
                continue;
            }

            if (!treatments.TryGetValue(item.Time.Value, out var treatment))
            {
                treatment = treatments[item.Time.Value] = new Treatment();
            }

            treatment.Carbs = item.Nutrition?.Carbohydrate?.Net;
            treatment.CreatedAt = item.Time;
            treatment.EnteredBy = "Tidepool";
        }

        // physical activity
        foreach (var act in activity)
        {
            if (!act.Time.HasValue)
            {
                continue;
            }

            if (!treatments.TryGetValue(act.Time.Value, out var treatment))
            {
                treatment = treatments[act.Time.Value] = new Treatment();
            }

            treatment.Notes = act.Name;
            treatment.Duration = act.Duration?.Value / 60;
            treatment.EventType = "Exercise";
            treatment.CreatedAt = act.Time;
            treatment.EnteredBy = "Tidepool";
        }

        // bg values
        foreach (var bgValue in bgValues)
        {
            if (!bgValue.Time.HasValue)
            {
                continue;
            }

            if (!treatments.TryGetValue(bgValue.Time.Value, out var treatment))
            {
                treatment = treatments[bgValue.Time.Value] = new Treatment();
            }

            var glucose = ConvertBgValue(bgValue.Units, nightScoutUnits, bgValue.Value);
            treatment.Glucose = glucose.ToString(CultureInfo.InvariantCulture);
            treatment.Units = nightScoutUnits;
            treatment.CreatedAt = bgValue.Time;
            treatment.EnteredBy = "Tidepool";
        }

        return await nightscout.AddTreatmentsAsync(treatments.Values);
    }

    private static double ConvertBgValue(string sourceUnit, string targetUnit, double value)
    {
        /*
         * https://tidepool.stoplight.io/docs/tidepool-api/7ca12b17275f0-units
         * The algorithm followed for conversion of blood glucose/related values from mg/dL to mmol/L is:
         * If units field is mg/dL divide the value by 18.01559 (the molar mass of glucose is 180.1559)
         * Store the resulting floating point precision value without rounding or truncation
         * The value has now been converted into mmol/L
         */

        const double factor = 18.01559;
        var validUnits = new[] { "mmol/l", "mmol", "mg/dl" }.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!validUnits.Contains(sourceUnit) || !validUnits.Contains(targetUnit))
        {
            throw new NotSupportedException($"Conversion from {sourceUnit} to {targetUnit} is not supported.");
        }

        return (sourceUnit.ToLower(), targetUnit.ToLower()) switch
        {
            ("mmol/l" or "mmol", "mg/dl") => value * factor,
            ("mg/dl", "mmol/l" or "mmol") => value / factor,
            _ => value
        };
    }
}