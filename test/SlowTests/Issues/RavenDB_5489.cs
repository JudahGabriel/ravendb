﻿using System;
using FastTests;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.NewClient.Client.Data;
using Raven.NewClient.Client.Exceptions;
using Raven.NewClient.Client.Indexes;
using Raven.NewClient.Data.Indexes;
using Raven.NewClient.Operations.Databases;
using Raven.NewClient.Operations.Databases.Indexes;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_5489 : RavenNewTestBase
    {
        [Fact]
        public async Task IfIndexEncountersCorruptionItShouldBeMarkedAsErrored()
        {
            using (var store = GetDocumentStore())
            {
                new Users_ByName().Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "John"
                    });

                    session.SaveChanges();
                }

                WaitForIndexing(store);

                Assert.Equal(IndexState.Normal, store.Admin.Send(new GetStatisticsOperation()).Indexes[0].State);

                var database = await GetDatabase(store.DefaultDatabase);
                var index = database.IndexStore.GetIndex(1);
                index._indexStorage._simulateCorruption = true;

                using (var session = store.OpenSession())
                {
                    session.Store(new User
                    {
                        Name = "Bob"
                    });

                    session.SaveChanges();
                }

                var result = SpinWait.SpinUntil(() => store.Admin.Send(new GetStatisticsOperation()).Indexes[0].State == IndexState.Error, TimeSpan.FromSeconds(5));
                Assert.True(result);

                using (var commands = store.Commands())
                {
                    var e = Assert.Throws<RavenException>(() => commands.Query(new Users_ByName().IndexName, new IndexQuery(store.Conventions)));
                    Assert.Contains("Simulated corruption", e.InnerException.Message);
                }

                var errors = store.Admin.Send(new GetIndexErrorsOperation(new[] { new Users_ByName().IndexName }));
                Assert.Equal(1, errors[0].Errors.Length);
                Assert.Contains("Simulated corruption", errors[0].Errors[0].Error);
            }
        }

        private class User
        {
            public string Name { get; set; }
        }

        private class Users_ByName : AbstractIndexCreationTask<User>
        {
            public Users_ByName()
            {
                Map = users => from u in users
                               select new
                               {
                                   u.Name
                               };
            }
        }
    }
}