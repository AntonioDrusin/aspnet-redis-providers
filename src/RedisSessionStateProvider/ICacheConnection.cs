//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.Threading.Tasks;
using System.Web.SessionState;

namespace Microsoft.Web.Redis
{
    internal interface ICacheConnection
    {
        KeyGenerator Keys { get; set; }
        Task SetAsync(ISessionStateItemCollection data, int sessionTimeout);
        Task UpdateExpiryTimeAsync(int timeToExpireInSeconds);
        Task<LockWithData> TryTakeWriteLockAndGetDataAsync(DateTime lockTime, int lockTimeout);
        Task<LockWithData> TryCheckWriteLockAndGetDataAsync();
        Task TryReleaseLockIfLockIdMatchAsync(object lockId, int sessionTimeout);
        Task TryRemoveAndReleaseLockAsync(object lockId);
        Task TryUpdateAndReleaseLockAsync(object lockId, ISessionStateItemCollection data, int sessionTimeout);
        TimeSpan GetLockAge(object lockId);
    }
}
