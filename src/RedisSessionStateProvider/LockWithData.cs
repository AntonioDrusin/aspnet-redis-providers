using System.Web.SessionState;

namespace Microsoft.Web.Redis
{
    public class LockWithData
    {
        public bool Success { get; set; }
        public object LockId { get; set; }
        public ISessionStateItemCollection Data { get; set; }
        public int SessionTimeout { get; set; }

    }
}