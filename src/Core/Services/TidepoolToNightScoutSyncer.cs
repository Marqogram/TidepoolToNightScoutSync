using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TidepoolToNightScoutSync.Core.Model.Nightscout;
using TidepoolToNightScoutSync.Core.Services.Nightscout;
using TidepoolToNightScoutSync.Core.Services.Tidepool;

namespace TidepoolToNightScoutSync.Core.Services
{
    public class TidepoolToNightScoutSyncer
    {
        private readonly ITidepoolClientFactory _factory;
        private readonly NightscoutClient _nightscout;
        private readonly TidepoolToNightScoutSyncerOptions _options;
        private ITidepoolClient? _tidepool;

        public TidepoolToNightScoutSyncer(
            ITidepoolClientFactory factory,
            NightscoutClient nightscout,
            IOptions<TidepoolToNightScoutSyncerOptions> options)
        {
            _factory = factory;
            _nightscout = nightscout;
            _options = options.Value;
        }

        /* -------------------- Profiles -------------------- */
        public async Task<Profile?> SyncProfiles(DateTime? since = null, DateTime? till = null)
        {
            var nfi = CultureInfo.InvariantCulture.NumberFormat;
            since ??= _options.Since ?? DateTime.Today;
            till ??= _options.Till;
            _tidepool ??= await _factory.CreateAsync();

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

            foreach (var (name, schedule) in setting.BasalSchedules)
            {
                profile.Store.TryAdd(name, new ProfileInfo());
                profile.Store[name].Basal.AddRange(schedule.Select(x => new Basal
                {
                    Time = TimeSpan.FromMilliseconds(x.Start).ToString(@"hh\:mm"),
                    TimeAsSeconds = (x.Start / 1000).ToString(),
                    Value = x.Rate.ToString(nfi)
                }));
            }

            foreach (var (name, targets) in setting.BgTargets)
            {
                profile.Store.TryAdd(name, new ProfileInfo());
                foreach (var target in targets)
                {
                    var time = TimeSpan.FromMilliseconds(target.Start).ToString(@"hh\:mm");
                    var seconds = (target.Start / 1000).ToString();

                    profile.Store[name].TargetLow.Add(new Target
                    {
                        Time = time,
                        TimeAsSeconds = seconds,
                        Value = _options.TargetLow.ToString(nfi)
                    });

                    profile.Store[name].TargetHigh.Add(new Target
                    {
                        Time = time,
                        TimeAsSeconds = seconds,
                        Value = (_options.TargetLow + target.Target).ToString(nfi)
                    });
                }
            }

            foreach (var (name, carbRatios) in setting.CarbRatios)
            {
                profile.Store.TryAdd(name, new ProfileInfo());
                profile.Store[name].Carbratio.AddRange(carbRatios.Select(x => new Carbratio
                {
                    Time = TimeSpan.FromMilliseconds(x.Start).ToString(@"hh\:mm"),
                    TimeAsSeconds = (x.Start / 1000).ToString(),
                    Value = x.Amount.ToString(nfi)
                }));
            }

            foreach (var (name, sensitivities) in setting.InsulinSensitivities)
            {
                profile.Store.TryAdd(name, new ProfileInfo());
                profile.Store[name].Sens.AddRange(sensitivities.Select(x => new Sen
                {
                    Time = TimeSpan.FromMilliseconds(x.Start).ToString(@"hh\:mm"),
                    TimeAsSeconds = (x.Start / 1000).ToString(),
                    Value = x.Amount.ToString(nfi)
                }));
            }

            var profiles = await _nightscout.GetProfiles();
            profile.Id = profiles.FirstOrDefault(x => x.Mills == profile.Mills)?.Id;

            return await _nightscout.SetProfile(profile);
        }

        /* -------------------- Treatments (bolus/food/exercise) -------------------- */
        public async Task<IReadOnlyList<Treatment>> SyncAsync(DateTime? since = null, DateTime? till = null)
        {
            since ??= _options.Since ?? DateTime.Today;
            till ??= _options.Till;
            _tidepool ??= await _factory.CreateAsync();

            var boluses = (await _tidepool.GetBolusAsync(since, till))
                .Where(x => x.Time.HasValue)
                .GroupBy(x => x.Time!.Value)
                .ToDictionary(x => x.Key, x => x.First());

            var food = (await _tidepool.GetFoodAsync(since, till))
                .Where(x => x.Time.HasValue)
                .GroupBy(x => x.Time!.Value)
                .ToDictionary(x => x.Key, x => x.First());

            var activity = await _tidepool.GetPhysicalActivityAsync(since, till);

            var treatments = new Dictionary<DateTime, Treatment>();

            foreach (var bolus in boluses.Values)
            {
                var time = bolus.Time!.Value;

                treatments[time] = new Treatment
                {
                    Insulin = bolus.Normal,
                    Duration = bolus.Duration?.TotalMinutes,
                    Relative = bolus.Extended,
                    Carbs = food.GetValueOrDefault(time)?.Nutrition?.Carbohydrate?.Net,
                    CreatedAt = time,
                    EnteredBy = "Tidepool"
                };
            }

            foreach (var item in food.Values)
            {
                var time = item.Time!.Value;

                if (!treatments.ContainsKey(time))
                {
                    treatments[time] = new Treatment
                    {
                        Carbs = item.Nutrition?.Carbohydrate?.Net,
                        CreatedAt = time,
                        EnteredBy = "Tidepool"
                    };
                }
            }

            foreach (var act in activity.Where(x => x.Time.HasValue))
            {
                var time = act.Time!.Value;

                treatments[time] = new Treatment
                {
                    EventType = "Exercise",
                    Notes = act.Name,
                    Duration = act.Duration?.Value / 60,
                    CreatedAt = time,
                    EnteredBy = "Tidepool"
                };
            }

            return await _nightscout.AddTreatmentsAsync(treatments.Values);
        }

        /* -------------------- CGM Entries (NEW) -------------------- */
        public async Task SyncCgmAsync(DateTime? since = null, DateTime? till = null)
        {
            since ??= _options.Since ?? DateTime.Today;
            till ??= _options.Till;
            _tidepool ??= await _factory.CreateAsync();

            var status = await _nightscout.GetStatus();
            var nightScoutUnits = status.Settings?.Units ?? "mg/dl";

            var bgValues = await _tidepool.GetBgValues(since, till);

            var entries = bgValues
                .Where(x => x.Time.HasValue)
                .Select(bg =>
                {
                    var glucose = ConvertBgValue(bg.Units, nightScoutUnits, bg.Value);
                    var time = bg.Time!.Value;

                    return new Entry
                    {
                        Type = "sgv",
                        Sgv = (int)Math.Round(glucose),
                        Date = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
                        DateString = time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        Device = "DexcomG6",
                        Direction = "Flat",
                        Noise = 1
                    };
                })
                .ToList();

            if (entries.Count > 0)
                await _nightscout.AddEntriesAsync(entries);
        }

        /* -------------------- BG Conversion -------------------- */
        private static double ConvertBgValue(string sourceUnit, string targetUnit, double value)
        {
            const double factor = 18.01559;
            var validUnits = new[] { "mg/dl", "mmol/l", "mmol" }.ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!validUnits.Contains(sourceUnit) || !validUnits.Contains(targetUnit))
                throw new NotSupportedException($"Conversion from {sourceUnit} to {targetUnit} is not supported.");

            return (sourceUnit.ToLower(), targetUnit.ToLower()) switch
            {
                ("mg/dl", "mmol/l" or "mmol") => value / factor,
                ("mmol/l" or "mmol", "mg/dl") => value * factor,
                _ => value
            };
        }
    }
}
