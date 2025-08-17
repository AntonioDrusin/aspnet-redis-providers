//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using FakeItEasy;
using System;
using System.Configuration;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.SessionState;
using Xunit;

namespace Microsoft.Web.Redis.Tests
{
    public class RedisConnectionWrapperTests
    {
        [Fact]
        public async Task UpdateExpiryTime_Valid()
        {
            const string sessionId = "session_id";
            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), sessionId);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            await redisConn.UpdateExpiryTimeAsync(90).ConfigureAwait(false);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 2),
                A<object[]>.That.Matches(o => o.Length == 1))).MustHaveHappened();
        }

        [Fact]
        public void GetLockAge_ValidTicks()
        {
            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), "");
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            var ticks = DateTime.Now.Ticks;
            Thread.Sleep(1000);
            (new PositiveTimeSpanValidator()).Validate(redisConn.GetLockAge(ticks));
        }

        [Fact]
        public void GetLockAge_InValidTicks()
        {
            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), "");
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            Assert.NotEqual(0, redisConn.GetLockAge("Invalid-tics").TotalHours);
        }

        [Fact]
        public async Task Set_ValidData()
        {
            const string sessionId = "session_id";
            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), sessionId);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            var data = new SessionStateItemCollection();
            data["key"] = "value";
            await redisConn.SetAsync(data, 90).ConfigureAwait(false);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 2),
                A<object[]>.That.Matches(o => o.Length == 2))).MustHaveHappened();
        }

        [Fact]
        public async Task TryTakeWriteLockAndGetData_UnableToLock()
        {
            const string id = "session_id";
            var lockTime = DateTime.Now;
            const int lockTimeout = 90;

            object[] returnFromRedis = { "Diff-lock-id", "", "15", true };

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                 A<object[]>.That.Matches(o => o.Length == 2))).Returns(Task.FromResult((object)returnFromRedis));
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).Returns("Diff-lock-id");
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).Returns(true);
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).Returns(15);

            var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
            Assert.False(result.Success);
            Assert.Equal("Diff-lock-id", result.LockId);
            Assert.Null(result.Data);
            Assert.Equal(15, result.SessionTimeout);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                A<object[]>.That.Matches(o => o.Length == 2))).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task TryTakeWriteLockAndGetData_UnableToLockWithSameLockId()
        {
            const string id = "session_id";
            var lockTime = DateTime.Now;
            const int lockTimeout = 90;

            object[] returnFromRedis = { lockTime.Ticks.ToString(), "", "15", true };

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            RedisConnectionWrapper redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                 A<object[]>.That.Matches(o => o.Length == 2))).Returns(Task.FromResult((object)returnFromRedis));
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).Returns(lockTime.Ticks.ToString());
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).Returns(true);
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).Returns(15);

            var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
            Assert.False(result.Success);
            Assert.Equal(lockTime.Ticks.ToString(), result.LockId);
            Assert.Null(result.Data);
            Assert.Equal(15, result.SessionTimeout);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                A<object[]>.That.Matches(o => o.Length == 2))).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).MustNotHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task TryTakeWriteLockAndGetData_Valid()
        {
            const string id = "session_id";
            var lockTime = DateTime.Now;
            const int lockTimeout = 90;

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            SessionStateItemCollection sessionDataReturn = new SessionStateItemCollection();
            sessionDataReturn["key1"] = "value1";
            sessionDataReturn["key2"] = "value2";

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            sessionDataReturn.Serialize(writer);

            var serializedSessionData = ms.ToArray();

            object[] sessionData = { "", serializedSessionData };
            object[] returnFromRedis = { lockTime.Ticks.ToString(), sessionData, "15", false };

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                 A<object[]>.That.Matches(o => o.Length == 2))).Returns(Task.FromResult((object)returnFromRedis));
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).Returns(lockTime.Ticks.ToString());
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).Returns(false);
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).Returns(sessionDataReturn);
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).Returns(15);

            var result = await redisConn.TryTakeWriteLockAndGetDataAsync(lockTime, lockTimeout).ConfigureAwait(false);
            Assert.True(result.Success);
            Assert.Equal(lockTime.Ticks.ToString(), result.LockId);
            Assert.Equal(2, result.Data.Count);
            Assert.Equal(15, result.SessionTimeout);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                A<object[]>.That.Matches(o => o.Length == 2))).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.IsLocked(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task TryCheckWriteLockAndGetData_Valid()
        {
            const string id = "session_id";

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            var sessionDataReturn = new SessionStateItemCollection();
            sessionDataReturn["key1"] = "value1";
            sessionDataReturn["key2"] = "value2";

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            sessionDataReturn.Serialize(writer);

            var serializedSessionData = ms.ToArray();

            object[] sessionData = { "", serializedSessionData };
            object[] returnFromRedis = { "", sessionData, "15" };

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                 A<object[]>.That.Matches(o => o.Length == 0))).Returns(Task.FromResult((object)returnFromRedis));
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).Returns("");
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).Returns(sessionDataReturn);
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).Returns(15);

            var result = await redisConn.TryCheckWriteLockAndGetDataAsync().ConfigureAwait(false);
            Assert.True(result.Success);
            Assert.Null(result.LockId);
            Assert.Equal(2, result.Data.Count);
            Assert.Equal(15, result.SessionTimeout);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                A<object[]>.That.Matches(o => o.Length == 0))).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetLockId(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionData(A<object>.Ignored)).MustHaveHappened();
            A.CallTo(() => redisConn.redisConnection.GetSessionTimeout(A<object>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task TryReleaseLockIfLockIdMatch_WriteLock()
        {
            const string id = "session_id";
            object lockId = DateTime.Now.Ticks;

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            await redisConn.TryReleaseLockIfLockIdMatchAsync(lockId, 900).ConfigureAwait(false);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3 && s[0].Equals(redisConn.Keys.LockKey)),
                 A<object[]>.That.Matches(o => o.Length == 2))).MustHaveHappened();
        }

        [Fact]
        public async Task TryRemoveIfLockIdMatch_Valid()
        {
            const string id = "session_id";
            object lockId = DateTime.Now.Ticks;

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();

            await redisConn.TryRemoveAndReleaseLockAsync(lockId).ConfigureAwait(false);
            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3),
                 A<object[]>.That.Matches(o => o.Length == 1))).MustHaveHappened();
        }

        [Fact]
        public async Task TrySetObjectNotMarkedSerializable()
        {
            const string id = "session_id";
            const int sessionTimeout = 900;
            object lockId = DateTime.Now.Ticks;
            var data = new SessionStateItemCollection();
            data["Key"] = new {Name = "Hal"}; // try to add anon type, this will throw a serialization error when you try to commit it as the type is not marked as serializable.

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            var exceptionTask = Assert.ThrowsAsync<HttpException>(() => redisConn.TryUpdateAndReleaseLockAsync(lockId, data, sessionTimeout));
            var exception = await exceptionTask.ConfigureAwait(false);
            Assert.Contains("Unable to serialize the session state.", exception.Message);
        }

        [Fact]
        public async Task TryUpdateIfLockIdMatchPrepare_NoUpdateNoDelete()
        {
            const string id = "session_id";
            const int sessionTimeout = 900;
            object lockId = DateTime.Now.Ticks;
            var data = new SessionStateItemCollection();

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            await redisConn.TryUpdateAndReleaseLockAsync(lockId, data, sessionTimeout).ConfigureAwait(false);

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3), A<object[]>.That.Matches(
               o => o.Length == 10 &&
                    o[2].Equals(0) &&
                    o[3].Equals(9) &&
                    o[4].Equals(8) &&
                    o[5].Equals(1) &&
                    o[6].Equals(9) &&
                    o[7].Equals(10)
                ))).MustHaveHappened();
        }

        [Fact]
        public async Task TryUpdateIfLockIdMatchPrepare_Valid_OneUpdateOneDelete()
        {
            const string id = "session_id";
            const int sessionTimeout = 900;
            object lockId = DateTime.Now.Ticks;
            var data = new SessionStateItemCollection();
            data["KeyDel"] = "valueDel";
            data["Key"] = "value";
            data.Remove("KeyDel");

            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            redisConn.redisConnection = A.Fake<IRedisClientConnection>();
            await redisConn.TryUpdateAndReleaseLockAsync(lockId, data, sessionTimeout).ConfigureAwait(false);

            A.CallTo(() => redisConn.redisConnection.EvalAsync(A<string>.Ignored, A<string[]>.That.Matches(s => s.Length == 3), A<object[]>.That.Matches(
               o => o.Length == 10 &&
                    o[2].Equals(0) &&
                    o[3].Equals(9) &&
                    o[4].Equals(8) &&
                    o[5].Equals(1) &&
                    o[6].Equals(9) &&
                    o[7].Equals(10)
                ))).MustHaveHappened();
        }

        [Fact]
        public void SerializationReturnsNull_IfValueIsNull()
        {
            const string id = "session_id";
            RedisConnectionWrapper.sharedConnection = A.Fake<RedisSharedConnection>();
            var redisConn = new RedisConnectionWrapper(Utility.GetDefaultConfigUtility(), id);
            var result = redisConn.SerializeSessionStateItemCollection(null);
            Assert.Null(result);
        }

        [Fact]
        public void DeserializationReturnsNull_IfValueIsNull()
        {
            var result = StackExchangeClientConnection.DeserializeSessionStateItemCollection(null);
            Assert.Null(result);
        }
    }
}