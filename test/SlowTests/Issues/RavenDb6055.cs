﻿using System;
using System.Threading.Tasks;
using FastTests;
using Raven.NewClient.Abstractions.Data;
using Raven.NewClient.Client;
using Raven.NewClient.Client.Linq;
using Raven.NewClient.Operations.Databases.Indexes;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDb6055 : RavenNewTestBase
    {
        private class User
        {
#pragma warning disable 169,649
            public string FirstName;
            public string LastName;
#pragma warning restore 169,649
        }

        [Fact(Skip = "RavenDB-6285")]
        public async Task CreatingNewAutoIndexWillDeleteSmallerOnes()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.Query<User>()
                        .Where(x => x.FirstName == "Alex")
                        .ToListAsync();

                    var indexes = await store.Admin.SendAsync(new GetIndexesOperation(0, 25));
                    Assert.Equal(1, indexes.Length);
                    Assert.Equal("Auto/Users/ByFirstName", indexes[0].Name);
                }

                var tcs = new TaskCompletionSource<object>();

                using ((await (await store.Changes().ConnectionTask)
                        .ForAllIndexes().Task)
                    .Subscribe(new SetTaskOnIndexDelete(tcs)))
                {
                    using (var session = store.OpenAsyncSession())
                    {
                        await session.Query<User>()
                            .Where(x => x.LastName == "Smith")
                            .ToListAsync();
                    }

                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(45)), tcs.Task);

                    var indexes = await store.Admin.SendAsync(new GetIndexesOperation(0, 25));
                    Assert.Equal("Auto/Users/ByFirstNameAndLastName", indexes[0].Name);
                }
            }
        }

        private class SetTaskOnIndexDelete : IObserver<IndexChange>
        {
            private readonly TaskCompletionSource<object> _tcs;

            public SetTaskOnIndexDelete(TaskCompletionSource<object> tcs)
            {
                _tcs = tcs;
            }

            public void OnCompleted()
            {

            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(IndexChange value)
            {
                if (value.Type == IndexChangeTypes.IndexRemoved)
                    _tcs.TrySetResult(value);
            }
        }
    }
}