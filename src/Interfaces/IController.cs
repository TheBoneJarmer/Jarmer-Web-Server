using System.Collections.Generic;
using AdvancedSockets.Http;

namespace Jarmer.WebServer.Interfaces
{
    public interface IController
    {
        HttpRequest Request { get; set; }
        HttpConnectionInfo ConnectionInfo { get; set; }
        HttpCookies Cookies { get; set; }
    }
}