using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SnowMaker
{
    public class UniqueIdGenerator : IUniqueIdGenerator
    {
        readonly IOptimisticDataStore _optimisticDataStore;
        readonly ConcurrentDictionary<string, ScopeState> _scopeStatesForIdGenerator = new ConcurrentDictionary<string, ScopeState>();

        readonly static SemaphoreSlim _scopeCreationSemaphore = new SemaphoreSlim(1,1); 
        readonly static SemaphoreSlim _semaphoreCreationSemaphore = new SemaphoreSlim(1, 1); 
        readonly static ConcurrentDictionary<string, SemaphoreSlim> _semaphoresForScopes = new ConcurrentDictionary<string, SemaphoreSlim>();
        
        int _batchSize = 100;
        int _maxWriteAttempts = 25;

        public UniqueIdGenerator(IOptimisticDataStore optimisticDataStore)
        {
            _optimisticDataStore = optimisticDataStore;
        }

        public int BatchSize
        {
            get { return _batchSize; }
            set { _batchSize = value; }
        }

        public int MaxWriteAttempts
        {
            get { return _maxWriteAttempts; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException("value", _maxWriteAttempts, "MaxWriteAttempts must be a positive number.");

                _maxWriteAttempts = value;
            }
        }

        public async Task<long> NextIdAsync(string scopeName)
        {
            var semaphoreSlim = await GetSemaphoreForScopeAsync(scopeName);
            await semaphoreSlim.WaitAsync();

            var state = await GetScopeStateAsync(scopeName);

            try
            {
                if (state.LastId == state.HighestIdAvailableInBatch)
                {
                    await UpdateFromSyncStoreAsync(scopeName, state);
                }

                return Interlocked.Increment(ref state.LastId);
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        async Task<ScopeState> GetScopeStateAsync(string scopeName)
        {
            return await _scopeStatesForIdGenerator.GetValueAsync(
                scopeName,
                _scopeCreationSemaphore,
            #pragma warning disable 1998
                async () =>
                {
                    return new ScopeState();
                });
            #pragma warning restore 1998

        }

        async Task<SemaphoreSlim> GetSemaphoreForScopeAsync(string scopeName)
        {
            return await _semaphoresForScopes.GetValueAsync(
                scopeName,
                _semaphoreCreationSemaphore, 
            #pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
                async () =>
                {
                    return new SemaphoreSlim(1,1);
                });
            #pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        }

        async Task UpdateFromSyncStoreAsync(string scopeName, ScopeState state)
        {
            var writesAttempted = 0;

            while (writesAttempted < _maxWriteAttempts)
            {
                var data = await _optimisticDataStore.GetDataAsync(scopeName);

                if (!long.TryParse(data, out long nextId))
                    throw new UniqueIdGenerationException(string.Format(
                       "The id seed returned from storage for scope '{0}' was corrupt, and could not be parsed as a long. The data returned was: {1}",
                       scopeName,
                       data));

                state.LastId = nextId - 1;
                state.HighestIdAvailableInBatch = nextId - 1 + _batchSize;
                var firstIdInNextBatch = state.HighestIdAvailableInBatch + 1;

                var written = await _optimisticDataStore.TryOptimisticWriteAsync(scopeName, firstIdInNextBatch.ToString(CultureInfo.InvariantCulture));

                if (written)
                    return;

                writesAttempted++;
            }

            throw new UniqueIdGenerationException(string.Format(
                "Failed to update the data store after {0} attempts. This likely represents too much contention against the store. Increase the batch size to a value more appropriate to your generation load.",
                writesAttempted));
        }
    }
}
