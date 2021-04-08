using System.Collections.Generic;
using System.Net;

namespace WebserverCS
{
    public abstract class ActionResult
    {
        public ActionResult()
        {
            
        }
    }

    public class JsonResult : ActionResult
    {
        public object Body { get; private set; }

        public JsonResult(object body) : base()
        {
            Body = body;
        }
    }

    public class TextResult : ActionResult
    {
        public string Text { get; private set; }

        public TextResult(string text) : base()
        {
            Text = text;
        } 
    }

    public class HtmlResult : ActionResult
    {
        public string Html { get; private set; }

        public HtmlResult(string html)
        {
            Html = html;
        }
    }

    public class ContentResult : ActionResult
    {
        public string Path { get; private set; }
        public ContentResult(string path)
        {
            Path = path;
        }
    }

    public class RedirectResult : ActionResult
    {
        public string Url { get; private set; }

        public RedirectResult(string url) : base()
        {
            Url = url;
        }
    }
}