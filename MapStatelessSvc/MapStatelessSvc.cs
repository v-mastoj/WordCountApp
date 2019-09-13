using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.ServiceFabric.Data;
using System.Net.Http;
using System.Net;
using OrchestratorStatefulSvc;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace MapStatelessSvc
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class MapStatelessSvc : StatelessService
    {
        public MapStatelessSvc(StatelessServiceContext context)
            : base(context)
        { }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new ServiceInstanceListener[]
            {
                new ServiceInstanceListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, "ServiceEndpoint", (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        return new WebHostBuilder()
                                    .UseKestrel()
                                    .ConfigureServices(
                                        services => services
                                            .AddSingleton<StatelessServiceContext>(serviceContext))
                                    .UseContentRoot(Directory.GetCurrentDirectory())
                                    .UseStartup<Startup>()
                                    .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.None)
                                    .UseUrls(url)
                                    .Build();
                    }))
            };
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            while(!cancellationToken.IsCancellationRequested)
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    string proxyUrl = $"http://localhost:19081/WordCount/OrchestratorStatefulSvc/api/Orchestrator/jobs";

                    using (HttpResponseMessage httpResponse = await httpClient.GetAsync($"{proxyUrl}?PartitionKey=0&PartitionKind=Int64Range"))
                    {
                        if (httpResponse.StatusCode != HttpStatusCode.OK)
                        {
                            continue;
                        }

                        MapTask task = JsonConvert.DeserializeObject<MapTask>(await httpResponse.Content.ReadAsStringAsync());

                        // Process the task
                        MatchCollection collection = Regex.Matches(task.Input, @"\b\w+\b");
                        task.Output = new Dictionary<string, int>();
                        foreach(Match match in collection)
                        {
                            string s = match.Value.ToLower();
                            if(task.Output.ContainsKey(s))
                            {
                                task.Output[s]++;
                            }
                            else
                            {
                                task.Output[s] = 1;
                            }

                        }

                        await Task.Delay(2000);

                        var serializedJson = JsonConvert.SerializeObject(task);
                        using (HttpResponseMessage putTaskResponse = await httpClient.PutAsync($"{proxyUrl}/{task.ParentJobUuid}?PartitionKey=0&PartitionKind=Int64Range", new StringContent(serializedJson, UnicodeEncoding.UTF8, "application/json")))
                        {
                            if (putTaskResponse.StatusCode != HttpStatusCode.OK)
                            {
                                // Error
                                ServiceEventSource.Current.ServiceMessage(this.Context, "Task completion failed");
                            }

                        }

                        ServiceEventSource.Current.ServiceMessage(this.Context, "Task{0}-Job{1} completed", task.Index, task.ParentJobUuid);

                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

                }
            }
        }
    }
}