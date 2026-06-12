// One VS instance at a time: collections each launch their own devenv via VsFixture, so they must
// run sequentially (the cross-file scenario lives in its own collection precisely so its heavy
// open/edit/close churn cannot destabilize the 100+ light scenarios sharing the main instance).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
