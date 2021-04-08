using System.Collections.Generic;
using AdvancedSockets.Http;

namespace WebserverCS.Interfaces
{
    public interface IController
    {
        HttpRequest Request { get; set; }
        HttpConnectionInfo ConnectionInfo { get; set; }
        HttpCookies Cookies { get; set; }
    }
}