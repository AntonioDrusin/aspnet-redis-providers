//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.IO;
using System.Threading.Tasks;
using System.Web.SessionState;
using Microsoft.Web.Redis.Tests;
using StackExchange.Redis;
using Xunit;

namespace Microsoft.Web.Redis.FunctionalTests
{
    public class RedisConnectionWrapperFunctionalTests
    {
        private static int uniqueSessionNumber = 1;

        private RedisConnectionWrapper GetRedisConnectionWrapperWithUniqueSession()
        {
            return GetRedisConnectionWrapperWithUniqueSession(Utility.GetDefaultConfigUtility());
        }

        private RedisConnectionWrapper GetRedisConnectionWrapperWithUniqueSession(ProviderConfiguration pc)
        {
            string id = Guid.NewGuid().ToString();
            uniqueSessionNumber++;
            // Initial connection with redis
            RedisConnectionWrapper.sharedConnection = null;
            RedisConnectionWrapper redisConn = new RedisConnectionWrapper(pc, id);
            return redisConn;
        }

        private void DisposeRedisConnectionWrapper(RedisConnectionWrapper redisConn)
        {
            RedisConnectionWrapper.sharedConnection = null;
        }

        [Fact()]
        public async Task Set_ValidData_WithCustomSerializer()
        {
            // this also tests host:port config part
            var pc = Utility.GetDefaultConfigUtility();
            pc.ApplicationName = "APPTEST";
            pc.Port = 6379;

            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession(pc);

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value",
                    ["key1"] = "value1"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                // Get actual connection and get data blob from redis
                var actualConnection = GetRealRedisConnection(redisConn);

                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection dataFromRedis = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                dataFromRedis = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value", dataFromRedis["key"]);
                Assert.Equal("value1", dataFromRedis["key1"]);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task Set_ValidData()
        {
            // this also tests host:port config part
            var pc = Utility.GetDefaultConfigUtility();
            pc.ApplicationName = "APPTEST";
            pc.Port = 6379;

            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession(pc);

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value",
                    ["key1"] = "value1"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                // Get actual connection and get data blob from redis
                var actualConnection = GetRealRedisConnection(redisConn);

                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection dataFromRedis = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                dataFromRedis = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value", dataFromRedis["key"]);
                Assert.Equal("value1", dataFromRedis["key1"]);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task Set_NullData()
        {
            // this also tests host:port config part
            var pc = Utility.GetDefaultConfigUtility();
            pc.ApplicationName = "APPTEST";
            pc.Port = 6379;

            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession(pc);

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value",
                    ["key1"] = null
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                // Get actual connection and get data blob from redis
                var actualConnection = GetRealRedisConnection(redisConn);

                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection dataFromRedis = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                dataFromRedis = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value", dataFromRedis["key"]);
                Assert.Null(dataFromRedis["key1"]);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task Set_ExpireData()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();
                // Inserting data into redis server that expires after 1 second
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 1).ConfigureAwait(false);

                // Wait for 2 seconds so that data will expire
                await Task.Delay(2000);

                // Get actual connection and get data blob from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedis = actualConnection.HashGetAll(redisConn.Keys.DataKey);

                // Check that data should not be there
                Assert.Empty(sessionDataFromRedis);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeWriteLockAndGetData_WithNullData()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = null
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                var lockTime = DateTime.Now;
                const int lockTimeout = 900;
                
                var result = await redisConn
                    .TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Single(result.Data);
                Assert.Null(result.Data["key"]);

                // Get actual connection and get data lock from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                string lockValueFromRedis = actualConnection.StringGet(redisConn.Keys.LockKey);
                Assert.Equal(lockTime.Ticks.ToString(), lockValueFromRedis);

                // remove data and lock from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeWriteLockAndGetData_WriteLockWithoutAnyOtherLock()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                var lockTime = DateTime.Now;
                const int lockTimeout = 900;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());

                Assert.Single(result.Data);

                // this will desirialize value
                Assert.Equal("value", result.Data["key"]);

                // Get actual connection and get data lock from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                string lockValueFromRedis = actualConnection.StringGet(redisConn.Keys.LockKey);
                Assert.Equal(lockTime.Ticks.ToString(), lockValueFromRedis);

                // remove data and lock from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeWriteLockAndGetData_WriteLockWithOtherWriteLock()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (RedisServer redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;

                // Takewrite lock successfully first time
                var firstLockTime = DateTime.Now;
                var firstLockResult = await redisConn.TryTakeWriteLockAndGetDataAsync(firstLockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(firstLockResult.Success);
                Assert.Equal(firstLockTime.Ticks.ToString(), firstLockResult.LockId);
                Assert.Single(firstLockResult.Data);

                // try to take write lock and fail and get earlier lock id
                var secondLockTime = firstLockTime.AddSeconds(1);
                var secondLockResult = await redisConn.TryTakeWriteLockAndGetDataAsync(secondLockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.False(secondLockResult.Success);
                Assert.Equal(firstLockTime.Ticks.ToString(), secondLockResult.LockId.ToString());
                Assert.Null(secondLockResult.Data);

                // Get actual connection
                var actualConnection = GetRealRedisConnection(redisConn);
                // remove data and lock from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeWriteLockAndGetData_WriteLockWithOtherWriteLockWithSameLockId()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                SessionStateItemCollection data = new SessionStateItemCollection();
                data["key"] = "value";
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                int lockTimeout = 900;
                // Same LockId
                DateTime lockTime = DateTime.Now;

                // Takewrite lock successfully first time
                var firstResult = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(firstResult.Success);
                Assert.Equal(lockTime.Ticks.ToString(), firstResult.LockId.ToString());
                Assert.Single(firstResult.Data);

                // try to take write lock and fail and get earlier lock id
                var secondResult = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.Equal(lockTime.Ticks.ToString(), secondResult.LockId.ToString());
                Assert.Null(secondResult.Data);

                // Get actual connection
                IDatabase actualConnection = GetRealRedisConnection(redisConn);
                // remove data and lock from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeReadLockAndGetData_WithoutAnyLock()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900);

                var result = await redisConn.TryCheckWriteLockAndGetDataAsync().ConfigureAwait(false);
                
                Assert.True(result.Success);
                Assert.Null(result.LockId);
                Assert.Single(result.Data);
                Assert.Equal("value", result.Data["key"]);

                // Get actual connection
                // remove data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeReadLockAndGetData_WithOtherWriteLock()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900);

                int lockTimeout = 900;

                DateTime firstLockTime = DateTime.Now;
                var firstResult = await redisConn.TryTakeWriteLockAndGetDataAsync(firstLockTime, lockTimeout).ConfigureAwait(false); 
                Assert.True(firstResult.Success);
                Assert.Equal(firstLockTime.Ticks.ToString(), firstResult.LockId.ToString());
                Assert.Single(firstResult.Data);

                var secondResult = await redisConn.TryCheckWriteLockAndGetDataAsync().ConfigureAwait(false);
                Assert.False(secondResult.Success);
                Assert.Equal(firstLockTime.Ticks.ToString(), secondResult.LockId.ToString());
                Assert.Null(secondResult.Data);

                // Get actual connection
                // remove data and lock from redis
                IDatabase actualConnection = GetRealRedisConnection(redisConn);
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryTakeWriteLockAndGetData_ExpireWriteLock()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900);

                const int lockTimeout = 1;

                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Single(result.Data);

                // Wait for 2 seconds so that lock will expire
                await Task.Delay(2000);

                // Get actual connection and check that lock do not exists
                IDatabase actualConnection = GetRealRedisConnection(redisConn);
                string lockValueFromRedis = actualConnection.StringGet(redisConn.Keys.LockKey);
                Assert.Null(lockValueFromRedis);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryReleaseLockIfLockIdMatch_ValidWriteLockRelease()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900);

                const int lockTimeout = 900;

                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Single(result.Data);

                await redisConn.TryReleaseLockIfLockIdMatchAsync(result.LockId, 900).ConfigureAwait(false);

                // Get actual connection and check that lock do not exists
                var actualConnection = GetRealRedisConnection(redisConn);
                string lockValueFromRedis = actualConnection.StringGet(redisConn.Keys.LockKey);
                Assert.Null(lockValueFromRedis);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryReleaseLockIfLockIdMatch_InvalidWriteLockRelease()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;

                DateTime lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Single(result.Data);

                object wrongLockId = lockTime.AddSeconds(1).Ticks.ToString();
                await redisConn.TryReleaseLockIfLockIdMatchAsync(wrongLockId, 900).ConfigureAwait(false);

                // Get actual connection and check that lock do not exists
                IDatabase actualConnection = GetRealRedisConnection(redisConn);
                string lockValueFromRedis = actualConnection.StringGet(redisConn.Keys.LockKey);
                Assert.Equal(result.LockId, lockValueFromRedis);

                // remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                actualConnection.KeyDelete(redisConn.Keys.LockKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryRemoveIfLockIdMatch_ValidLockIdAndRemove()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Single(result.Data);

                await redisConn.TryRemoveAndReleaseLockAsync(result.LockId).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.DataKey));

                // check lock removed from redis
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateIfLockIdMatch_WithValidUpdateAndDelete()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key1"] = "value1",
                    ["key2"] = "value2",
                    ["key3"] = "value3"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Equal(3, result.Data.Count);
                Assert.Equal("value1", result.Data["key1"]);
                Assert.Equal("value2", result.Data["key2"]);
                Assert.Equal("value3", result.Data["key3"]);

                result.Data["key2"] = "value2-updated";
                result.Data.Remove("key3");
                await redisConn.TryUpdateAndReleaseLockAsync(result.LockId, result.Data, 900).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection dataFromRedis2 = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                dataFromRedis2 = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value1", dataFromRedis2["key1"]);
                Assert.Equal("value2-updated", dataFromRedis2["key2"]);

                // check lock removed and remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateIfLockIdMatch_WithOnlyUpdateAndNoDelete()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key1"] = "value1",
                    ["key2"] = "value2",
                    ["key3"] = "value3"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                
                Assert.Equal(3, result.Data.Count);
                Assert.Equal("value1", result.Data["key1"]);
                Assert.Equal("value2", result.Data["key2"]);
                Assert.Equal("value3", result.Data["key3"]);

                result.Data["key2"] = "value2-updated";
                await redisConn.TryUpdateAndReleaseLockAsync(result.LockId, result.Data, 900).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection sessionDataFromRedisAsCollection = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                sessionDataFromRedisAsCollection = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value1", sessionDataFromRedisAsCollection["key1"]);
                Assert.Equal("value2-updated", sessionDataFromRedisAsCollection["key2"]);
                Assert.Equal("value3", sessionDataFromRedisAsCollection["key3"]);

                // check lock removed and remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateIfLockIdMatch_WithNoUpdateAndOnlyDelete()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key1"] = "value1",
                    ["key2"] = "value2",
                    ["key3"] = "value3"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 900;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Equal(lockTime.Ticks.ToString(), result.LockId.ToString());
                Assert.Equal(3, result.Data.Count);
                Assert.Equal("value1", result.Data["key1"]);
                Assert.Equal("value2", result.Data["key2"]);
                Assert.Equal("value3", result.Data["key3"]);

                result.Data.Remove("key3");
                await redisConn.TryUpdateAndReleaseLockAsync(result.LockId, result.Data, 900).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                SessionStateItemCollection sessionDataFromRedisAsCollection = null;
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                sessionDataFromRedisAsCollection = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value1", sessionDataFromRedisAsCollection["key1"]);
                Assert.Equal("value2", sessionDataFromRedisAsCollection["key2"]);

                // check lock removed and remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateIfLockIdMatch_ExpiryTime_OnValidData()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value",
                    ["key1"] = "value1"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                // Check that data shoud exists
                const int lockTimeout = 90;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout);
                Assert.Equal(2, result.Data.Count);

                // Update expiry time to only 1 sec and than verify that.
                await redisConn.TryUpdateAndReleaseLockAsync(result.LockId, result.Data, 1);

                // Wait for 1.1 seconds so that data will expire
                await Task.Delay(2000);

                // Get data blob from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedisAfterExpire = actualConnection.HashGetAll(redisConn.Keys.DataKey);

                // Check that data shoud not be there
                Assert.Empty(sessionDataFromRedisAfterExpire);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateAndReleaseLockIfLockIdMatch_LargeLockTime_ExpireManuallyTest()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key1"] = "value1"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                const int lockTimeout = 120000;
                var lockTime = DateTime.Now;
                var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout)
                    .ConfigureAwait(false);
                Assert.True(result.Success);
                await redisConn.TryUpdateAndReleaseLockAsync(result.LockId, result.Data, 900).ConfigureAwait(false);

                // Get actual connection and check that lock is released
                var actualConnection = GetRealRedisConnection(redisConn);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryRemoveIfLockIdMatch_NullLockId()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key"] = "value"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                var result = await redisConn.TryCheckWriteLockAndGetDataAsync().ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Null(result.LockId);
                Assert.Single(result.Data);
                
                await redisConn.TryRemoveAndReleaseLockAsync(null).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.DataKey));

                // check lock removed from redis
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        [Fact()]
        public async Task TryUpdateIfLockIdMatch_LockIdNull()
        {
            var pc = Utility.GetDefaultConfigUtility();
            using (var redisServer = new RedisServer())
            {
                var redisConn = GetRedisConnectionWrapperWithUniqueSession();

                // Inserting data into redis server
                var data = new SessionStateItemCollection
                {
                    ["key1"] = "value1"
                };
                await redisConn.SetAsync(data, 900).ConfigureAwait(false);

                var result = await redisConn.TryCheckWriteLockAndGetDataAsync().ConfigureAwait(false);
                Assert.True(result.Success);
                Assert.Null(result.LockId);
                Assert.Single(result.Data);

                // update session data without lock id (to support lock free session)
                result.Data["key1"] = "value1-updated";
                await redisConn.TryUpdateAndReleaseLockAsync(null, result.Data, 900).ConfigureAwait(false);

                // Get actual connection and get data from redis
                var actualConnection = GetRealRedisConnection(redisConn);
                var sessionDataFromRedis = actualConnection.StringGet(redisConn.Keys.DataKey);
                var ms = new MemoryStream(sessionDataFromRedis);
                var reader = new BinaryReader(ms);
                var sessionDataFromRedisAsCollection = SessionStateItemCollection.Deserialize(reader);

                Assert.Equal("value1-updated", sessionDataFromRedisAsCollection["key1"]);

                // check lock removed and remove data from redis
                actualConnection.KeyDelete(redisConn.Keys.DataKey);
                Assert.False(actualConnection.KeyExists(redisConn.Keys.LockKey));
                DisposeRedisConnectionWrapper(redisConn);
            }
        }

        private IDatabase GetRealRedisConnection(RedisConnectionWrapper redisConn)
        {
            return (IDatabase)((StackExchangeClientConnection)redisConn.redisConnection).RealConnection;
        }
    }
}