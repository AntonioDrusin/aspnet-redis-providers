//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Web.SessionState;
using StackExchange.Redis;

namespace Microsoft.Web.Redis
{
    internal class StackExchangeClientConnection : IRedisClientConnection
    {
        private ProviderConfiguration _configuration;
        private RedisSharedConnection _sharedConnection;

        public StackExchangeClientConnection(ProviderConfiguration configuration, RedisSharedConnection sharedConnection)
        {
            _configuration = configuration;
            _sharedConnection = sharedConnection;
        }

        // This is used just by tests
        public IDatabase RealConnection
        {
            get { return _sharedConnection.Connection; }
        }

        public Task<bool> ExpiryAsync(string key, int timeInSeconds)
        {
            var timeSpan = new TimeSpan(0, 0, timeInSeconds);
            return RetryLogicAsync(() => RealConnection.KeyExpireAsync(key, timeSpan));
        }

        public Task<object> EvalAsync(string script, string[] keyArgs, object[] valueArgs)
        {
            var redisKeyArgs = new RedisKey[keyArgs.Length];
            var redisValueArgs = new RedisValue[valueArgs.Length];

            var i = 0;
            foreach (string key in keyArgs)
            {
                redisKeyArgs[i] = key;
                i++;
            }

            i = 0;
            foreach (object val in valueArgs)
            {
                if (val.GetType() == typeof(byte[]))
                {
                    // User data is always in bytes
                    redisValueArgs[i] = (byte[])val;
                }
                else
                {
                    // Internal data like session timeout and indexes are stored as strings
                    redisValueArgs[i] = val.ToString();
                }
                i++;
            }
            return RetryLogicAsync<object>(async () => await RealConnection.ScriptEvaluateAsync(script, redisKeyArgs, redisValueArgs).ConfigureAwait(false));
        }

        private async Task<T> OperationExecutorAsync<T>(Func<Task<T>> redisOperation)
        {
            try
            {
                return await redisOperation().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Try once as this can be caused by force reconnect by closing multiplexer
                return await redisOperation().ConfigureAwait(false);
            }
            catch (RedisConnectionException)
            {
                // Try once after reconnect
                _sharedConnection.ForceReconnect();
                return await redisOperation().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("NOSCRIPT"))
                {
                    // Second call should pass if it was script not found issue
                    return await redisOperation().ConfigureAwait(false);
                }
                throw;
            }
        }

        /// <summary>
        /// If retry timout is provide than we will retry first time after 20 ms and after that every 1 sec till retry timout is expired or we get value.
        /// </summary>
        private async Task<T> RetryLogicAsync<T>(Func<Task<T>> redisOperation)
        {
            int timeToSleepBeforeRetryInMiliseconds = 20;
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return await OperationExecutorAsync(redisOperation).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (_configuration.RetryTimeout < timer.Elapsed)
                    {
                        LogUtility.LogError($"Exception: {e.Message}");
                        throw;
                    }
                    else
                    {
                        int remainingTimeout = (int)(_configuration.RetryTimeout - timer.Elapsed).TotalMilliseconds;
                        // if remaining time is less than 1 sec than wait only for that much time and than give a last try
                        if (remainingTimeout < timeToSleepBeforeRetryInMiliseconds)
                        {
                            timeToSleepBeforeRetryInMiliseconds = remainingTimeout;
                        }
                    }

                    // First time try after 20 msec after that try after 1 second
                    await Task.Delay(timeToSleepBeforeRetryInMiliseconds).ConfigureAwait(false);
                    timeToSleepBeforeRetryInMiliseconds = 1000;
                }
            }
        }

        public int GetSessionTimeout(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            Debug.Assert(lockScriptReturnValueArray[2] != null);
            int sessionTimeout = (int)lockScriptReturnValueArray[2];
            if (sessionTimeout == -1)
            {
                sessionTimeout = (int)_configuration.SessionTimeout.TotalSeconds;
            }
            // converting seconds to minutes
            sessionTimeout = sessionTimeout / 60;
            return sessionTimeout;
        }

        public bool IsLocked(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            Debug.Assert(lockScriptReturnValueArray[3] != null);
            return (bool)lockScriptReturnValueArray[3];
        }

        public string GetLockId(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            return (string)lockScriptReturnValueArray[0];
        }

        public ISessionStateItemCollection GetSessionData(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);

            SessionStateItemCollection sessionData = null;
            if (lockScriptReturnValueArray.Length > 1 && lockScriptReturnValueArray[1] != null)
            {
                RedisResult data = lockScriptReturnValueArray[1];
                var serializedSessionStateItemCollection = data;

                if (serializedSessionStateItemCollection != null)
                {
                    sessionData = DeserializeSessionStateItemCollection(serializedSessionStateItemCollection);
                }
            }
            return sessionData;
        }


        public static SessionStateItemCollection DeserializeSessionStateItemCollection(RedisResult serializedSessionStateItemCollection)
        {
            try
            {
                MemoryStream ms = new MemoryStream((byte[])serializedSessionStateItemCollection);
                BinaryReader reader = new BinaryReader(ms);
                return SessionStateItemCollection.Deserialize(reader);
            }
            catch
            {
                return null;
            }
        }

        public Task SetAsync(string key, byte[] data, DateTime utcExpiry)
        {
            RedisValue redisValue = data;
            var timeSpanForExpiry = utcExpiry - DateTime.UtcNow;
            return OperationExecutorAsync( async () => await RealConnection.StringSetAsync(key, redisValue, timeSpanForExpiry).ConfigureAwait(false));
        }

        public async Task<byte[]> GetAsync(string key)
        {
            var redisValue = await OperationExecutorAsync(async () => await RealConnection.StringGetAsync(key).ConfigureAwait(false)).ConfigureAwait(false);
            return (byte[])redisValue;
        }

        public Task RemoveAsync(string key)
        {
            RedisKey redisKey = key;
            return OperationExecutorAsync(async () => await RealConnection.KeyDeleteAsync(redisKey).ConfigureAwait(false));
        }

        public byte[] GetOutputCacheDataFromResult(object rowDataFromRedis)
        {
            var rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            return (byte[])rowDataAsRedisResult;
        }
    }
}