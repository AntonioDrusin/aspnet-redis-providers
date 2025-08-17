//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using Xunit;
using FakeItEasy;
using System.Web.SessionState;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNet.SessionState;

namespace Microsoft.Web.Redis.Tests
{
    public class RedisSessionStateProviderTests
    {
        [Fact]
        public void Initialize_WithNullConfig()
        {
            RedisSessionStateProvider sessionStateStore = new RedisSessionStateProvider();
            Assert.Throws<ArgumentNullException>(() => sessionStateStore.Initialize(null, null));
        }

        [Fact]
        public async Task EndRequest_Successful()
        {
            Utility.SetConfigUtilityToDefault();
            var mockCache = A.Fake<ICacheConnection>();
            RedisSessionStateProvider sessionStateStore = new RedisSessionStateProvider();
            sessionStateStore.sessionId = "session-id";
            sessionStateStore.sessionLockId = "session-lock-id";
            sessionStateStore.cache = mockCache;
            await sessionStateStore.EndRequestAsync(null);
            A.CallTo(() => mockCache.TryReleaseLockIfLockIdMatchAsync(A<object>.Ignored, A<int>.Ignored))
                .MustHaveHappened();
        }

        [Fact]
        public void CreateNewStoreData_WithEmptyStore()
        {
            Utility.SetConfigUtilityToDefault();
            SessionStateStoreData sssd = new SessionStateStoreData(Utility.SessionStateItemCollection(), null, 900);
            RedisSessionStateProvider sessionStateStore = new RedisSessionStateProvider();
            Assert.True(Utility.CompareSessionStateStoreData(sessionStateStore.CreateNewStoreData(null, 900), sssd));
        }

        [Fact]
        public async Task CreateUninitializedItem_Successful()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            await sessionStateStore.CreateUninitializedItemAsync(null, id, 15, CancellationToken.None);

            A.CallTo(() => mockCache.SetAsync(A<ISessionStateItemCollection>.That.Matches(o =>
                o.Count == 1 && SessionStateActions.InitializeItem.Equals(o["SessionStateActions"])
            ), 900)).MustHaveHappened();
        }

        [Fact]
        public async Task GetItem_NullFromStore()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var mockCache = A.Fake<ICacheConnection>();
            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync()).Returns(new LockWithData
            {
                Success = true,
                LockId = 0,
                SessionTimeout = 0,
                Data = null,
            });
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            var data = await sessionStateStore.GetItemAsync(null, id, CancellationToken.None);

            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync()).MustHaveHappened();
            A.CallTo(() => mockCache.TryReleaseLockIfLockIdMatchAsync(0, A<int>.Ignored)).MustHaveHappened();
            Assert.Null(data.Item);
            Assert.False(data.Locked);
            Assert.Equal(TimeSpan.Zero, data.LockAge);
            Assert.Equal(0, data.LockId);
        }

        [Fact]
        public async Task GetItem_RecordLocked()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";

            var mockCache = A.Fake<ICacheConnection>();
            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync()).Returns(new LockWithData
            {
                Success = false,
                LockId = 0,
                Data = null,
                SessionTimeout = 0,
            });
            A.CallTo(() => mockCache.GetLockAge(A<object>.Ignored)).Returns(TimeSpan.Zero);

            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            var data = await sessionStateStore.GetItemAsync(null, id, CancellationToken.None);
            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync()).MustHaveHappened();
            A.CallTo(() => mockCache.GetLockAge(A<object>.Ignored)).MustHaveHappened();

            Assert.Null(data.Item);
            Assert.True(data.Locked);
        }

        [Fact]
        public async Task GetItem_RecordFound()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";

            ISessionStateItemCollection sessionStateItemCollection = Utility.SessionStateItemCollection();
            sessionStateItemCollection["session-key"] = "session-value";
            sessionStateItemCollection["SessionStateActions"] = SessionStateActions.None;
            var sssd = new SessionStateStoreData(sessionStateItemCollection, null, 15);

            ISessionStateItemCollection sessionData = Utility.SessionStateItemCollection();
            sessionData["session-key"] = "session-value";
            sessionData["SessionStateActions"] = SessionStateActions.None;
            var mockCache = A.Fake<ICacheConnection>();
            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync())
                .Returns(new LockWithData
                {
                    Success = true,
                    LockId = 0,
                    Data = sessionData,
                    SessionTimeout = (int)RedisSessionStateProvider.configuration.SessionTimeout.TotalMinutes
                });

            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            var data = await sessionStateStore.GetItemAsync(null, id, CancellationToken.None);
            var sessionStateStoreData = data.Item;

            A.CallTo(() => mockCache.TryCheckWriteLockAndGetDataAsync()).MustHaveHappened();
            Assert.True(Utility.CompareSessionStateStoreData(sessionStateStoreData, sssd));
            Assert.False(data.Locked);
            Assert.Equal(TimeSpan.Zero, data.LockAge);
            Assert.Equal(SessionStateActions.None, data.Actions);
        }

        [Fact]
        public async Task GetItemExclusive_RecordLocked()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";

            var mockCache = A.Fake<ICacheConnection>();
            A.CallTo(() => mockCache.TryTakeWriteLockAndGetDataAsync(A<DateTime>.Ignored, 90)).Returns(new LockWithData
            {
                Success = false,
                LockId = 0,
                Data = null,
                SessionTimeout = 0,
            });
            A.CallTo(() => mockCache.GetLockAge(A<object>.Ignored)).Returns(TimeSpan.Zero);


            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            var data = await sessionStateStore.GetItemExclusiveAsync(null, id, CancellationToken.None);
            var sessionStateStoreData = data.Item;

            A.CallTo(() => mockCache.TryTakeWriteLockAndGetDataAsync(A<DateTime>.Ignored, 90)).MustHaveHappened();
            A.CallTo(() => mockCache.GetLockAge(A<object>.Ignored)).MustHaveHappened();

            Assert.Null(sessionStateStoreData);
            Assert.True(data.Locked);
        }

        [Fact]
        public async Task GetItemExclusive_RecordFound()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";

            ISessionStateItemCollection sessionStateItemCollection = Utility.SessionStateItemCollection();
            sessionStateItemCollection["session-key"] = "session-value";
            SessionStateStoreData sssd = new SessionStateStoreData(sessionStateItemCollection, null, 15);

            ISessionStateItemCollection sessionData = Utility.SessionStateItemCollection();
            sessionData["session-key"] = "session-value";

            var mockCache = A.Fake<ICacheConnection>();
            A.CallTo(() => mockCache.TryTakeWriteLockAndGetDataAsync(A<DateTime>.Ignored, 90))
                .Returns(new LockWithData
                {
                    Success = true,
                    LockId = 0,
                    Data = sessionData,
                    SessionTimeout = (int)RedisSessionStateProvider.configuration.SessionTimeout.TotalMinutes
                });

            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };

            var data = await sessionStateStore.GetItemExclusiveAsync(null, id, CancellationToken.None);
            var sessionStateStoreData = data.Item;

            A.CallTo(() =>
                mockCache.TryTakeWriteLockAndGetDataAsync(A<DateTime>.Ignored, 90)).MustHaveHappened();

            Assert.True(Utility.CompareSessionStateStoreData(sessionStateStoreData, sssd));
            Assert.False(data.Locked);
            Assert.Equal(TimeSpan.Zero, data.LockAge);
            Assert.Equal(SessionStateActions.None, data.Actions);
        }

        [Fact]
        public async Task ResetItemTimeout_Successful()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var mockCache = A.Fake<ICacheConnection>();

            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.ResetItemTimeoutAsync(null, id, CancellationToken.None);
            A.CallTo(() => mockCache.UpdateExpiryTimeAsync(900)).MustHaveHappened();
        }

        [Fact]
        public async Task RemoveItem_Successful()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.RemoveItemAsync(null, id, "lockId", null, CancellationToken.None);
            A.CallTo(() => mockCache.TryRemoveAndReleaseLockAsync(A<object>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task ReleaseItemExclusive_Successful()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.ReleaseItemExclusiveAsync(null, id, "lockId", CancellationToken.None);
            A.CallTo(() => mockCache.TryReleaseLockIfLockIdMatchAsync(A<object>.Ignored, A<int>.Ignored)).MustHaveHappened();
        }

        [Fact]
        public async Task SetAndReleaseItemExclusive_NewItemNullItems()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var sssd = new SessionStateStoreData(null, null, 15);

            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.SetAndReleaseItemExclusiveAsync(null, id, sssd, null, true, CancellationToken.None);
            A.CallTo(() => mockCache.SetAsync(A<ISessionStateItemCollection>.That.Matches(o => o.Count == 0), 900))
                .MustHaveHappened();
        }

        [Fact]
        public async Task SetAndReleaseItemExclusive_NewItemValidItems()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var sessionStateItemCollection = Utility.SessionStateItemCollection();
            sessionStateItemCollection["session-key"] = "session-value";
            var sssd = new SessionStateStoreData(sessionStateItemCollection, null, 15);

            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.SetAndReleaseItemExclusiveAsync(null, id, sssd, null, true, CancellationToken.None);
            A.CallTo(() =>
                mockCache.SetAsync(A<ISessionStateItemCollection>.That.Matches(o => o.Count == 1 && o["session-key"] != null
                ), 900)).MustHaveHappened();
        }

        [Fact]
        public async Task SetAndReleaseItemExclusive_OldItemNullItems()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var sssd = new SessionStateStoreData(null, null, 900);

            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.SetAndReleaseItemExclusiveAsync(null, id, sssd, 7, false, CancellationToken.None);
            A.CallTo(() =>
                    mockCache.TryUpdateAndReleaseLockAsync(A<object>.Ignored, A<ISessionStateItemCollection>.Ignored, 900))
                .MustNotHaveHappened();
        }

        [Fact]
        public async Task SetAndReleaseItemExclusive_OldItemRemovedItems()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var sessionStateItemCollection = Utility.SessionStateItemCollection();
            sessionStateItemCollection["session-key"] = "session-val";
            sessionStateItemCollection.Remove("session-key");
            var sssd = new SessionStateStoreData(sessionStateItemCollection, null, 15);

            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.SetAndReleaseItemExclusiveAsync(null, id, sssd, 7, false, CancellationToken.None);
            A.CallTo(() => mockCache.TryUpdateAndReleaseLockAsync(A<object>.Ignored,
                A<SessionStateItemCollection>.That.Matches(o => o.Count == 0), 900)).MustHaveHappened();
        }

        [Fact]
        public async Task SetAndReleaseItemExclusive_OldItemInsertedItems()
        {
            Utility.SetConfigUtilityToDefault();
            const string id = "session-id";
            var sessionStateItemCollection = Utility.SessionStateItemCollection();
            sessionStateItemCollection["session-key"] = "session-value";
            var sssd = new SessionStateStoreData(sessionStateItemCollection, null, 15);

            var mockCache = A.Fake<ICacheConnection>();
            var sessionStateStore = new RedisSessionStateProvider
            {
                cache = mockCache
            };
            await sessionStateStore.SetAndReleaseItemExclusiveAsync(null, id, sssd, 7, false, CancellationToken.None);
            A.CallTo(() => mockCache.TryUpdateAndReleaseLockAsync(A<object>.Ignored,
                A<SessionStateItemCollection>.That.Matches(o => o.Count == 1), 900)).MustHaveHappened();
        }
    }
}