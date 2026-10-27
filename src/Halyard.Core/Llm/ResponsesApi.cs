using System.Collections.Generic;
using System.Text;
using Halyard.Core.Serialization;

namespace Halyard.Core.Llm
{
    /// <summary>
    /// Halyard's chat messages to and from the OpenAI Responses API (<c>POST .../responses</c>), for gateways that
    /// serve only that format. Text only, no streaming, no tools. Mirrors the Fleet router's translator
    /// (Fairlead <c>Protocol/ResponsesApi.cs</c>).
    /// </summary>
    public static class ResponsesApi
    {
        /// <summary>
        /// <c>{model, input:[{role, content:[{type, text}]}], max_output_tokens}</c>: system, developer and user text is
        /// <c>input_text</c>, assistant text is <c>output_text</c>. An empty model is left out (the URL names it).
        /// </summary>
        public static JObject BuildRequest(IEnumerable<LlmMessage> messages, string model, int maxOutputTokens)
        {
            var input = new JArray();
            foreach (var m in messages)
            {
                if (m == null || string.IsNullOrEmpty(m.Content)) continue;
                var role = (m.Role ?? "user").Trim().ToLowerInvariant();
                if (role != "system" && role != "developer" && role != "assistant") role = "user";
                var partType = role == "assistant" ? "output_text" : "input_text";
                var content = new JArray();
                content.Add(new JObject { { "type", partType }, { "text", m.Content } });
                input.Add(new JObject { { "role", role }, { "content", content } });
            }
            var body = new JObject();
            if (!string.IsNullOrWhiteSpace(model)) body["model"] = model.Trim();
            body["input"] = input;
            if (maxOutputTokens > 0) body["max_output_tokens"] = maxOutputTokens;
            return body;
        }

        /// <summary>
        /// The answer text: every <c>output_text</c> part of every message item in <c>output</c>, else the top-level
        /// <c>output_text</c> string; null when the response holds neither. Reasoning and tool items are skipped.
        /// </summary>
        public static string Text(JToken response)
        {
            var o = response as JObject;
            if (o == null) return null;
            var sb = new StringBuilder();
            var any = false;
            var output = o["output"] as JArray;
            if (output != null)
            {
                foreach (var item in output)
                {
                    var io = item as JObject;
                    if (io == null) continue;
                    var type = Str(io["type"]);
                    if (type != null && type != "message") continue;
                    var parts = io["content"] as JArray;
                    if (parts == null) continue;
                    foreach (var part in parts)
                    {
                        var po = part as JObject;
                        if (po == null) continue;
                        var pt = Str(po["type"]);
                        if (pt != "output_text" && pt != "text") continue;
                        var text = Str(po["text"]);
                        if (text == null) continue;
                        sb.Append(text);
                        any = true;
                    }
                }
            }
            if (any) return sb.ToString();
            return Str(o["output_text"]);
        }

        private static string Str(JToken token)
        {
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }
    }
}
