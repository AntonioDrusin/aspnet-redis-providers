//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.IO;
using System.Threading.Tasks;
using System.Web.Caching;

namespace Microsoft.Web.Redis
{
    internal class RedisOutputCacheConnectionWrapper : IOutputCacheConnection
    {
        internal static RedisSharedConnection sharedConnection;
        private static object lockForSharedConnection = new object();

        internal IRedisClientConnection redisConnection;
        private ProviderConfiguration configuration;

        public RedisOutputCacheConnectionWrapper(ProviderConfiguration configuration)
        {
            this.configuration = configuration;

            // only single object of RedisSharedConnection will be created and then reused
            if (sharedConnection == null)
            {
                lock (lockForSharedConnection)
                {
                    if (sharedConnection == null)
                    {
                        sharedConnection = new RedisSharedConnection(configuration);
                    }
                }
            }
            redisConnection = new StackExchangeClientConnection(configuration, sharedConnection);
        }

        /*-------Start of Add operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS = { key }
        // ARGV = { page data, expiry time in miliseconds }
        // retArray = { page data from cache or new }
        private static readonly string addScript = (@"
                    local retVal = redis.call('GET',KEYS[1])
                    if retVal == false then
                       redis.call('PSETEX',KEYS[1],ARGV[2],ARGV[1])
                       retVal = ARGV[1]
                    end
                    return retVal
                    ");

        public async Task<object> AddAsync(string key, object entry, DateTime utcExpiry)
        {
            key = GetKeyForRedis(key);
            var expiryTime = utcExpiry - DateTime.UtcNow;
            var keyArgs = new string[] { key };
            var valueArgs = new object[] {
                SerializeOutputCacheEntry(entry),
                (long) expiryTime.TotalMilliseconds };

            var rowDataFromRedis = await redisConnection.EvalAsync(addScript, keyArgs, valueArgs).ConfigureAwait(false);
            return DeserializeOutputCacheEntry((byte[])rowDataFromRedis);
        }

        /*-------End of Add operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        public Task SetAsync(string key, object entry, DateTime utcExpiry)
        {
            key = GetKeyForRedis(key);
            byte[] data = SerializeOutputCacheEntry(entry);

            return redisConnection.SetAsync(key, data, utcExpiry);
        }

        public async Task<object> GetAsync(string key)
        {
            key = GetKeyForRedis(key);

            var data = await redisConnection.GetAsync(key).ConfigureAwait(false);
            return DeserializeOutputCacheEntry(data);
        }

        public Task RemoveAsync(string key)
        {
            key = GetKeyForRedis(key);
            return redisConnection.RemoveAsync(key);
        }

        private string GetKeyForRedis(string key)
        {
            return configuration.ApplicationName + "_" + key;
        }

        private byte[] SerializeOutputCacheEntry(object outputCacheEntry)
        {
            try
            {
                MemoryStream ms = new MemoryStream();
                OutputCache.Serialize(ms, outputCacheEntry);
                return ms.ToArray();
            }
            catch (ArgumentException)
            {
                LogUtility.LogWarning("{0} is not one of the specified output-cache types.", outputCacheEntry);
                return null;
            }
        }

        private object DeserializeOutputCacheEntry(byte[] serializedOutputCacheEntry)
        {
            try
            {
                MemoryStream ms = new MemoryStream(serializedOutputCacheEntry);
                return OutputCache.Deserialize(ms);
            }
            catch (ArgumentException)
            {
                LogUtility.LogWarning("The output cache entry is not one of the specified output-cache types.");
                return null;
            }
        }
    }
}