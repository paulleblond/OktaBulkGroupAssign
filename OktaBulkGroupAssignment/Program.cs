using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json.Linq;

namespace OktaBulkGroupAssignment
{
    [Command(
    ExtendedHelpText = @"
Note: This will only assign to groups that are OKTA_GROUP type.  AD mastered groups will not work.

The CSV must have no headers, and have the user login in the first column, and the group name in the second.  Some example data:

user@domain.dev,group1
otheruser@domain.dev,""group two""
admin@domain.dev,group1
"
)]
    public class Program
    {
        private static HttpClient client = new HttpClient();

        public static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);

        #region Parameters
        [Option(ShortName = "key", SymbolName = "k", Description = "The api key for your Okta tenant.  The key must have both read and write access to groups/users.")]
        public string ApiKey { get; }

        [Option(ShortName = "tenant", SymbolName = "t", Description = "The domain of your Okta tenant.  IE: mytenant.okta.com")]
        public string Tenant { get; }

        [Option(ShortName = "import", SymbolName = "i", Description = "The path to the comma separated file of groups and users.  The csv requires 2 columns: user login (email) and a group name.  One assignment per line.")]
        public string ImportPath { get; set; }

        [Option(ShortName = "verbose", SymbolName = "v", Description = "Verbose option.")]
        public bool Verbose { get; set; }
        #endregion

        private async Task OnExecuteAsync()
        {
            try
            {

                #region Validation
                if (string.IsNullOrEmpty(ApiKey))
                {
                    Console.WriteLine("The API key is required.  See help for details.");
                    return;
                }
                if (string.IsNullOrEmpty(Tenant))
                {
                    Console.WriteLine("The tenant url is required.  See help for details.");
                    return;
                }
                if (string.IsNullOrEmpty(ImportPath))
                {
                    Console.WriteLine("The path to the csv file is required.  See help for details.");
                    return;
                }
                #endregion

                using (var reader = new StreamReader(ImportPath))
                {
                    if (reader == null)
                    {
                        Console.WriteLine($"Unable to open file: {ImportPath}");
                        return;
                    }
                    using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                    {
                        csv.Configuration.HasHeaderRecord = false;
                        var records = csv.GetRecords<Assignment>().ToList();
                        records.ForEach(r => r.Assigned = false);

                        client.BaseAddress = new Uri($"https://{Tenant}/api/v1/");
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SSWS", ApiKey);

                        Console.WriteLine("Gathering Group Guids...");
                        foreach (string group in records.Select(r => r.GroupName).Distinct())
                        {
                            Log($"Looking up {group}");

                            var resp = await client.GetAsync($"groups?q={group}");

                            if (!resp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"Unable to get group guid for {group}");
                                Console.WriteLine(resp.ReasonPhrase);
                                string content = await resp.Content.ReadAsStringAsync();
                                Console.WriteLine(content);
                            }
                            else
                            {
                                string content = await resp.Content.ReadAsStringAsync();
                                JArray obj = JArray.Parse(content);
                                if (obj.Count != 1)
                                {
                                    Console.WriteLine($"Got unexpected result while looking up guid for group {group}");
                                    Console.WriteLine(content);
                                }
                                else
                                {
                                    string guid = obj[0]["id"].ToString();
                                    Log($"Found {guid}");
                                    records.ForEach(r => { if (r.GroupName == group) r.GroupGuid = guid; });
                                }
                            }
                        }

                        Console.WriteLine("Gathering User Guids...");
                        foreach (string login in records.Select(r => r.Login).Distinct())
                        {
                            Log($"Looking up {login}");
                            var resp = await client.GetAsync($"users/{login}");

                            if (!resp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"Unable to get user guid for {login}");
                                Console.WriteLine(resp.ReasonPhrase);
                                string content = await resp.Content.ReadAsStringAsync();
                                Console.WriteLine(content);
                            }
                            else
                            {
                                string content = await resp.Content.ReadAsStringAsync();
                                JObject obj = JObject.Parse(content);

                                string guid = obj["id"].ToString();
                                Log($"Found {guid}");
                                records.ForEach(r => { if (r.Login == login) r.UserGuid = guid; });

                            }
                        }

                        Console.WriteLine("Making group assignments...");
                        foreach (Assignment a in records.Where(r => !string.IsNullOrEmpty(r.UserGuid) && !string.IsNullOrEmpty(r.GroupGuid)))
                        {
                            Log($"Assigning {a.Login} ({a.UserGuid}) to {a.GroupName} ({a.GroupGuid})");
                            var resp = await client.PutAsync($"groups/{a.GroupGuid}/users/{a.UserGuid}",new StringContent(""));

                            if (!resp.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"Unable to assign {a.Login} ({a.UserGuid}) to {a.GroupName} ({a.GroupGuid})");
                                Console.WriteLine(resp.ReasonPhrase);
                                string content = await resp.Content.ReadAsStringAsync();
                                Console.WriteLine(content);
                            }
                            else
                            {
                                Log("Assignment successful");
                                a.Assigned = true;
                            }
                        }

                        Console.WriteLine($"Finished making {records.Where(r => r.Assigned).Count()} assignments.");

                        if (records.Where(r => !r.Assigned).Any())
                        {
                            Console.WriteLine("The following users were not assigned due to errors:");
                            foreach (Assignment a in records.Where(r => !r.Assigned))
                            {
                                Console.WriteLine($"{a.Login},\"{a.GroupName}\"");
                            }
                        }

                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("Unexpected Error:");
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.InnerException?.Message);
            }

        }

        private void Log(string msg) { if (Verbose) Console.WriteLine(msg); }
    }

    public class Assignment
    {
        [Index(0)]
        public string Login { get; set; }

        [Index(1)]
        public string GroupName { get; set; }

        [Ignore]
        public string UserGuid { get; set; }

        [Ignore]
        public string GroupGuid { get; set; }

        [Ignore]
        public bool Assigned { get; set; }
    }
}
