using System.Collections.Generic;
using System.Net;

namespace Jarmer.WebServer
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

    public class RedirectResult : ActionResult
    {
        public string Url { get; private set; }

        public RedirectResult(string url) : base()
        {
            Url = url;
        }
    }
}