//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.SessionState;

namespace Microsoft.Web.Redis
{
    internal class RedisConnectionWrapper : ICacheConnection
    {
        internal static RedisSharedConnection sharedConnection;
        private static object lockForSharedConnection = new object();

        public KeyGenerator Keys { set; get; }

        internal IRedisClientConnection redisConnection;
        private ProviderConfiguration configuration;

        public RedisConnectionWrapper(ProviderConfiguration configuration, string id)
        {
            this.configuration = configuration;
            Keys = new KeyGenerator(id, configuration.ApplicationName);

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

        public TimeSpan GetLockAge(object lockId)
        {
            // This method do not use redis
            string lockDateTimeTicksFromLockId = lockId.ToString();
            long lockTimeTicks;
            if (long.TryParse(lockDateTimeTicksFromLockId, out lockTimeTicks))
            {
                return DateTime.Now.Subtract(new DateTime(lockTimeTicks));
            }
            else
            { //lock id is not valid so release item exclusive should be called so make lock age very large
                return DateTime.Now.Subtract(new DateTime());
            }
        }

        /*-------Start of UpdateExpiryTime operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS[1] = = data-id, internal-id
        // ARGV[1] = session-timeout
        // this order should not change LUA script depends on it
        // if data doesn't exists then do nothing
        private static readonly string updateExpiryTimeScript = (@"
                local dataExists = redis.call('EXISTS', KEYS[1])
                if dataExists == 0 then
                    return 1;
                end

                local SessionTimeout = redis.call('GET', KEYS[2])
                if SessionTimeout ~= false then
                    redis.call('EXPIRE',KEYS[1], SessionTimeout)
                    redis.call('EXPIRE',KEYS[2], SessionTimeout)
                else
                    redis.call('EXPIRE',KEYS[1],ARGV[1])
                    redis.call('SET', KEYS[2], ARGV[1])
                    redis.call('EXPIRE',KEYS[2],ARGV[1])
                end
                return 1"
                );

        public Task UpdateExpiryTimeAsync(int timeToExpireInSeconds)
        {
            string[] keyArgs = new string[] { Keys.DataKey, Keys.InternalKey };
            object[] valueArgs = new object[1];
            valueArgs[0] = timeToExpireInSeconds;

            return redisConnection.EvalAsync(updateExpiryTimeScript, keyArgs, valueArgs);
        }

        /*-------End of UpdateExpiryTime operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        /*-------Start of Set operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS[1] = = data-id, internal-id
        // ARGV[1] = serialized session state, ARGV[2] = session-timeout
        // this order should not change LUA script depends on it
        private static readonly string setScript = (@"
                redis.call('SET', KEYS[1], ARGV[1])
                redis.call('EXPIRE',KEYS[1],ARGV[2])
                redis.call('SET', KEYS[2], ARGV[2])
                redis.call('EXPIRE',KEYS[2],ARGV[2])
                return 1"
                );

        private bool SetPrepare(ISessionStateItemCollection data, int sessionTimeout, out string[] keyArgs, out object[] valueArgs)
        {
            keyArgs = null;
            valueArgs = null;
            try
            {
                byte[] serializedSessionStateItemCollection = SerializeSessionStateItemCollection(data);

                keyArgs = new string[] { Keys.DataKey, Keys.InternalKey };

                valueArgs = new object[] { serializedSessionStateItemCollection, sessionTimeout };
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal byte[] SerializeSessionStateItemCollection(ISessionStateItemCollection sessionStateItemCollection)
        {
            if (sessionStateItemCollection is null)
            {
                return null;
            }
            MemoryStream ms = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(ms);
            ((SessionStateItemCollection)sessionStateItemCollection).Serialize(writer);
            writer.Close();
            return ms.ToArray();
        }

        public Task SetAsync(ISessionStateItemCollection data, int sessionTimeout)
        {
            if (SetPrepare(data, sessionTimeout, out var keyArgs, out var valueArgs))
            {
                return redisConnection.EvalAsync(setScript, keyArgs, valueArgs);
            }
            return Task.CompletedTask;
        }

        /*-------End of Set operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        /*-------Start of Lock set operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS = { write-lock-id, data-id, internal-id }
        // ARGV = { write-lock-value-that-we-want-to-set, request-timout }
        // lockValue = 1) (Initially) write lock value that we want to set (ARGV[1]) if we get lock successfully this will return as retArray[1]
        //             2) If another write lock exists than its lock value from cache
        // retArray = {lockValue , session data if lock was taken successfully, session timeout value if exists, wheather lock was taken or not}
        private static readonly string writeLockAndGetDataScript = (@"
                local retArray = {}
                local lockValue = ARGV[1]
                local locked = redis.call('SETNX',KEYS[1],ARGV[1])
                local IsLocked = true

                if locked == 0 then
                    lockValue = redis.call('GET',KEYS[1])
                else
                    redis.call('EXPIRE',KEYS[1],ARGV[2])
                    IsLocked = false
                end

                retArray[1] = lockValue
                if lockValue == ARGV[1] then retArray[2] = redis.call('GET',KEYS[2]) else retArray[2] = '' end

                local SessionTimeout = redis.call('GET',KEYS[3])
                if SessionTimeout ~= false then
                    retArray[3] = SessionTimeout
                    redis.call('EXPIRE',KEYS[2], SessionTimeout)
                    redis.call('EXPIRE',KEYS[3], SessionTimeout)
                else
                    retArray[3] = '-1'
                end

                retArray[4] = IsLocked
                return retArray
                ");

        public async Task<LockWithData> TryTakeWriteLockAndGetDataAsync(DateTime lockTime, int lockTimeout)
        {
            var expectedLockId = lockTime.Ticks.ToString();
            var keyArgs = new string[] { Keys.LockKey, Keys.DataKey, Keys.InternalKey };
            var valueArgs = new object[] { expectedLockId, lockTimeout };

            var rowDataFromRedis = await redisConnection.EvalAsync(writeLockAndGetDataScript, keyArgs, valueArgs).ConfigureAwait(false);

            var ret = new LockWithData
            {
                LockId = redisConnection.GetLockId(rowDataFromRedis),
                SessionTimeout = redisConnection.GetSessionTimeout(rowDataFromRedis)
            };

            var isLocked = redisConnection.IsLocked(rowDataFromRedis);
            if (!isLocked && ret.LockId.ToString().Equals(expectedLockId))
            {
                ret.Success = true;
                ret.Data = redisConnection.GetSessionData(rowDataFromRedis);
            }
            return ret;
        }

        // KEYS = { write-lock-id, data-id, internal-id }
        // ARGV = { }
        // lockValue = 1) (Initially) read lock value that we want to set (ARGV[1]) if we get lock successfully this will return as retArray[1]
        //             3) If write lock exists than its lock value from cache
        // retArray = {lockValue , session data if lock does not exist}
        private static readonly string readLockAndGetDataScript = (@"
                    local retArray = {}
                    local lockValue = ''
                    local writeLockValue = redis.call('GET',KEYS[1])
                    if writeLockValue ~= false then
                       lockValue = writeLockValue
                    end
                    retArray[1] = lockValue
                    if lockValue == '' then retArray[2] = redis.call('GET',KEYS[2]) else retArray[2] = '' end

                    local SessionTimeout = redis.call('GET', KEYS[3])
                    if SessionTimeout ~= false then
                        retArray[3] = SessionTimeout
                        redis.call('EXPIRE',KEYS[2], SessionTimeout)
                        redis.call('EXPIRE',KEYS[3], SessionTimeout)
                    else
                        retArray[3] = '-1'
                    end
                    return retArray
                    ");

        public async Task<LockWithData> TryCheckWriteLockAndGetDataAsync()
        {
            string[] keyArgs = new string[] { Keys.LockKey, Keys.DataKey, Keys.InternalKey };
            object[] valueArgs = new object[] { };

            object rowDataFromRedis = await redisConnection.EvalAsync(readLockAndGetDataScript, keyArgs, valueArgs).ConfigureAwait(false);

            var ret = new LockWithData
            {
                LockId = redisConnection.GetLockId(rowDataFromRedis),
                SessionTimeout = redisConnection.GetSessionTimeout(rowDataFromRedis)
            };

            if (ret.LockId.ToString().Equals(""))
            {
                // If lockId = "" means no lock exists and we got data from store.
                ret.LockId = null;
                ret.Success = true;
                ret.Data = redisConnection.GetSessionData(rowDataFromRedis);
            }
            return ret;
        }

        /*-------End of Lock set operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        /*-------Start of Lock release operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        public Task TryReleaseLockIfLockIdMatchAsync(object lockId, int sessionTimeout)
        {
            string[] keyArgs = { Keys.LockKey, Keys.DataKey, Keys.InternalKey };
            object[] valueArgs = { lockId, sessionTimeout };
            return redisConnection.EvalAsync(releaseWriteLockIfLockMatchScript, keyArgs, valueArgs);
        }

        // KEYS[1] = write-lock-id, KEYS[2] = data-id, KEYS[3] = internal-id
        // ARGV = { write-lock-value }, ARGV[2] = session time out
        private static readonly string releaseWriteLockIfLockMatchScript = (@"
                local writeLockValueFromCache = redis.call('GET',KEYS[1])
                if writeLockValueFromCache == ARGV[1] then
                    redis.call('DEL',KEYS[1])
                end
                local SessionTimeout = redis.call('GET', KEYS[3])
                if SessionTimeout ~= false then
                    redis.call('EXPIRE',KEYS[2], SessionTimeout)
                    redis.call('EXPIRE',KEYS[3], SessionTimeout)
                else
                    redis.call('EXPIRE',KEYS[2],ARGV[2])
                end
                return 1
                ");

        /*-------End of Lock release operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS = { write-lock-id, data-id, internal-id}
        // ARGV = { write-lock-value }
        private static readonly string removeSessionScript = (@"
                if ARGV[1] ~= '' then
                    local lockValue = redis.call('GET',KEYS[1])
                    if lockValue ~=  ARGV[1] then
                        return 1
                    end
                end
                redis.call('DEL',KEYS[2])
                redis.call('DEL',KEYS[3])
                redis.call('DEL',KEYS[1])
                ");

        public Task TryRemoveAndReleaseLockAsync(object lockId)
        {
            string[] keyArgs = { Keys.LockKey, Keys.DataKey, Keys.InternalKey };
            lockId = lockId ?? "";
            object[] valueArgs = { lockId.ToString() };
            return redisConnection.EvalAsync(removeSessionScript, keyArgs, valueArgs);
        }

        /*-------Start of TryUpdate operation-----------------------------------------------------------------------------------------------------------------------------------------------*/

        // KEYS[1] = write-lock-id, KEYS[2] = data-id, KEYS[3] = internal-id
        // ARGV[1] = write-lock-value, ARGV[2] = session time out,
        // ARGV[3] = number of items removed, ARGV[4] = number of items removed start index in ARGV, ARGV[5] = number of items removed end index in ARGV,
        // ARGV[6] = number of items updated, ARGV[7] = number of items updated start index in ARGV, ARGV[8] = number of items updated end index in ARGV,
        // ARGV[9...] = actual data
        // this order should not change LUA script depends on it
        private static readonly string removeAndUpdateSessionDataScript = (@"
                if ARGV[1] ~= '' then
                    local writeLockValueFromCache = redis.call('GET',KEYS[1])
                    if writeLockValueFromCache ~= ARGV[1] then
                        return 1
                    end
                end
                if tonumber(ARGV[6]) ~= 0 then redis.call('SET', KEYS[2], ARGV[10]) end
                redis.call('EXPIRE',KEYS[2],ARGV[2])
                redis.call('SET', KEYS[3], ARGV[2])
                redis.call('EXPIRE',KEYS[3],ARGV[2])
                redis.call('DEL',KEYS[1])");

        private bool TryUpdateAndReleaseLockPrepare(object lockId, ISessionStateItemCollection data, int sessionTimeout, out string[] keyArgs, out object[] valueArgs)
        {
            keyArgs = null;
            valueArgs = null;
            if (data != null)
            {
                List<object> list = new List<object>();
                int noOfItemsRemoved = 0;
                int noOfItemsUpdated = 0;
                byte[] serializedSessionStateItemCollection = SerializeSessionStateItemCollection(data);
                list.Add("SessionState");
                list.Add(serializedSessionStateItemCollection);
                noOfItemsUpdated = 1;

                keyArgs = new string[] { Keys.LockKey, Keys.DataKey, Keys.InternalKey };
                valueArgs = new object[list.Count + 8]; // this +8 is for first wight values in ARGV that we will add now
                valueArgs[0] = lockId ?? "";
                valueArgs[1] = sessionTimeout;
                valueArgs[2] = noOfItemsRemoved;
                valueArgs[3] = 9; // In Lua index starts from 1 so first item deleted will be 9th.
                valueArgs[4] = noOfItemsRemoved + 8; // index for last removed item
                valueArgs[5] = noOfItemsUpdated;
                valueArgs[6] = noOfItemsRemoved + 9; // first item updated will be next to last item removed
                valueArgs[7] = list.Count + 8; // index for last item in list in LUA

                // if nothing is changed in session then also execute update script to update session timeout
                if (list.Count != 0)
                {
                    list.CopyTo(valueArgs, 8);
                }
                return true;
            }
            return false;
        }

        public Task TryUpdateAndReleaseLockAsync(object lockId, ISessionStateItemCollection data, int sessionTimeout)
        {
            if (TryUpdateAndReleaseLockPrepare(lockId, data, sessionTimeout, out var keyArgs, out var valueArgs))
            {
                return redisConnection.EvalAsync(removeAndUpdateSessionDataScript, keyArgs, valueArgs);
            }
            return Task.CompletedTask;
        }

        /*-------End of TryUpdateIfLockIdMatch operation-----------------------------------------------------------------------------------------------------------------------------------------------*/
    }
}