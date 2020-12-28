using System;
using System.Net.Http;
using AdvancedSockets.Http;

namespace Jarmer.WebServer
{
    public abstract class HttpAttribute : Attribute
    {
        public HttpMethod Method { get; private set; }
        public string Path { get; private set; }

        public HttpAttribute(HttpMethod method, string path)
        {
            Method = method;
            Path = path;
        }
    }

    public class GetAttribute : HttpAttribute
    {
        public GetAttribute(string path) : base(HttpMethod.Get, path)
        {
            
        }
    }

    public class PostAttribute : HttpAttribute
    {
        public PostAttribute(string path) : base(HttpMethod.Post, path)
        {

        }
    }

    public class ContentTypeAttribute : Attribute
    {
        public string ContentType { get; private set; }

        public ContentTypeAttribute(SupportedContentType contentType)
        {
            if (contentType == SupportedContentType.MultipartFormData) ContentType = "multipart/form-data";
            if (contentType == SupportedContentType.FormUrlEncoded) ContentType = "application/x-www-form-urlencoded";
            if (contentType == SupportedContentType.Json) ContentType = "application/json";
        }
    }

    public abstract class CustomWebAttribute : Attribute
    {
        public abstract ActionResult Handle(HttpRequest request);
    }
}