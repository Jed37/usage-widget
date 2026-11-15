using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

// A local protocol fixture. It never opens a network connection or reads credentials.
static class MockCodex
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Reply(object message) { Console.WriteLine(Json.Serialize(message)); Console.Out.Flush(); }
    static object Credit(string id, long expiration) {
        return new { id, title = "Fixture full reset", description = "Test data", status = "available",
            resetType = "codexRateLimits", grantedAt = 1700000000, expiresAt = expiration };
    }
    static void Main()
    {
        string log = Environment.GetEnvironmentVariable("WIDGET_MOCK_LOG");
        string state = Environment.GetEnvironmentVariable("WIDGET_MOCK_STATE");
        if (string.IsNullOrEmpty(log) || string.IsNullOrEmpty(state)) return;
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var request = Json.Deserialize<Dictionary<string, object>>(line);
            string method = Convert.ToString(request["method"]);
            object id;
            if (!request.TryGetValue("id", out id)) continue;
            if (method == "initialize") { Reply(new { id, result = new { } }); continue; }
            File.AppendAllText(log, line + Environment.NewLine);
            string scenario = Environment.GetEnvironmentVariable("WIDGET_MOCK_SCENARIO") ?? "reset";
            if (scenario == "rpcError") { Reply(new { id, error = new { code = -32001, message = "Fixture API failure" } }); continue; }
            if (method == "account/rateLimits/read")
            {
                var parameters = (Dictionary<string, object>)request["params"];
                bool exclude = Convert.ToBoolean(parameters["excludeResetCreditDetails"]);
                string consumed = File.Exists(state) ? File.ReadAllText(state) : "";
                var credits = new List<object>();
                if (consumed != "all" && consumed != "fixture-A") credits.Add(Credit("fixture-A", 2000000000));
                if (consumed != "all") credits.Add(Credit("fixture-B", 2100000000));
                Reply(new { id, result = new {
                    rateLimits = new { planType = "plus", primary = new { usedPercent = 64, resetsAt = 2000000000 },
                        secondary = new { usedPercent = 25, resetsAt = 2000000000 } },
                    rateLimitResetCredits = new { availableCount = credits.Count, credits = exclude ? null : credits }
                } });
            }
            else if (method == "account/rateLimitResetCredit/consume")
            {
                var parameters = (Dictionary<string, object>)request["params"];
                if (!parameters.ContainsKey("creditId") || !parameters.ContainsKey("idempotencyKey"))
                    throw new Exception("Redemption must include the selected ID and idempotency key");
                if (scenario == "lostReply") { Thread.Sleep(30000); continue; }
                if (scenario == "reset" || scenario == "alreadyRedeemed") File.WriteAllText(state, Convert.ToString(parameters["creditId"]));
                if (scenario == "noCredit") File.WriteAllText(state, "all");
                Reply(new { id, result = new { outcome = scenario } });
            }
            else throw new Exception("Unexpected method: " + method);
        }
    }
}
