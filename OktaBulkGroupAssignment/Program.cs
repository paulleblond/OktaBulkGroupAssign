using System;
using System.Collections.Generic;
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
Note: For user import, go to your Okta instance's Users->People, choose 'More Actions', 'Import Users from CSV', and download the template.  This will ensure any custom user fields your tenant have defined are correctly added to the header.  Use this filled out CSV for uploading users.

Group assignment will only assign to groups that are OKTA_GROUP type.  AD mastered groups will not work.

The group assignment CSV must have no headers, and have the user login in the first column, and the group name in the second.  Some example data:

user@domain.dev,group1
otheruser@domain.dev,""group two""
admin@domain.dev,group1
"
)]
    public class Program
    {
        private static HttpClient client = new HttpClient();
        private string action;
        private string apiKey;
        private string tenant;
        private string importPath;


        public static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);

        #region Parameters
        [Argument(0, Description = "'import' to import new users 'assign' to make group assignments")]
        public string Action { get; set; }

        [Option(ShortName = "key", SymbolName = "k", Description = "The api key for your Okta tenant.  The key must have both read and write access to groups/users.")]
        public string ApiKey { get; }

        [Option(ShortName = "tenant", SymbolName = "t", Description = "The domain of your Okta tenant.  IE: mytenant.okta.com")]
        public string Tenant { get; }

        [Option(ShortName = "import", SymbolName = "i", Description = "The path to the comma separated file of groups and users.  The csv requires 2 columns: user login (email) and a group name.  One assignment per line.")]
        public string ImportPath { get; set; }

        [Option(ShortName = "verbose", SymbolName = "v", Description = "Verbose option.")]
        public bool Verbose { get; set; }

        [Option(ShortName = "activate", SymbolName = "a", Description = "Activates user upon creation.  Only applies to import.")]
        public bool Activate { get; set; }
        #endregion

        private async Task OnExecuteAsync()
        {
            try
            {
                action = Action?.ToLower();
                apiKey = ApiKey?.ToLower();
                tenant = Tenant?.ToLower();
                importPath = ImportPath?.ToLower();

                #region Validation
                while (action != "import" && action != "assign" && action != "quit")
                {
                    Console.WriteLine("What action do you want to take? <import, assign, quit>");
                    action = Console.ReadLine().ToLower();
                }
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("What is your API Key?");
                    apiKey = Console.ReadLine();
                }
                if (string.IsNullOrEmpty(tenant))
                {
                    Console.WriteLine("What is your tenant URL? (mytenant.okta.com)");
                    tenant = Console.ReadLine();
                }
                if (string.IsNullOrEmpty(importPath))
                {
                    Console.WriteLine("What is the path to the import/assignment file?");
                    importPath = Console.ReadLine();
                }
                #endregion

                using (var reader = new StreamReader(importPath))
                {
                    if (reader == null)
                    {
                        Console.WriteLine($"Unable to open file: {importPath}");
                        return;
                    }

                    client.BaseAddress = new Uri($"https://{tenant}/api/v1/");
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("SSWS", apiKey);

                    using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                    {
                        switch(action)
                        {
                            case "assign":
                                await AssignGroups(csv);
                                break;
                            case "import":
                                await ImportUsers(csv);
                                break;
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

        private async Task ImportUsers(CsvReader csv)
        {
            csv.Configuration.HasHeaderRecord = true;
            csv.Read();
            csv.ReadHeader();

            Dictionary<int, string> columns = new Dictionary<int, string>();
            JObject jUser = new JObject();
            JObject jProfile = new JObject();
            jUser.Add("profile", jProfile);
            string fieldname;
            for (int i = 0;  csv.TryGetField(i, out fieldname); i++)
            {
                columns.Add(i, fieldname);
                jProfile.Add(fieldname, null);
            }

            Dictionary<string,bool> created = new Dictionary<string,bool>();
            string postParams = "?activate=false";
            if (Activate)
            {
                postParams = "?activate=true";
            }

            while (csv.Read())
            {
                foreach (int index in columns.Keys)
                {
                    jProfile[columns[index]] = csv.GetField(index);
                }

                Log("Built user: " + jUser.ToString());

                Console.WriteLine($"Uploading {jProfile["login"].ToString()}");
                created.Add(jProfile["login"].ToString(), false);
                var resp = await client.PostAsync($"users{postParams}",new StringContent(jUser.ToString(),System.Text.Encoding.UTF8,"application/json"));

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Unable to create {jProfile["login"].ToString()}");
                    Console.WriteLine(resp.ReasonPhrase);
                    string content = await resp.Content.ReadAsStringAsync();
                    Console.WriteLine(content);
                }
                else
                {
                    created[jProfile["login"].ToString()] = true;
                }
            }

            Console.WriteLine($"Finished creating {created.Where(c => c.Value).Count()} accounts.");

            if (created.Where(c => !c.Value).Any())
            {
                Console.WriteLine("The following users were not created due to errors:");
                foreach (string a in created.Where(c => !c.Value).Select(s => s.Key))
                {
                    Console.WriteLine(a);
                }
            }
        }

        private async Task AssignGroups(CsvReader csv)
        {
            csv.Configuration.HasHeaderRecord = false;
            var records = csv.GetRecords<Assignment>().ToList();
            records.ForEach(r => r.Assigned = false);

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
                var resp = await client.PutAsync($"groups/{a.GroupGuid}/users/{a.UserGuid}", new StringContent(""));

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
