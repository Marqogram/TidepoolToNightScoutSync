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
        private ITidepoolCl
