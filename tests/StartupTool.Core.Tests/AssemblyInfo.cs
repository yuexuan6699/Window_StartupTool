// 这些测试会真实创建进程、命名管道与 Job 对象，
// 并行执行会在高负载机器上互相拖慢导致时序断言抖动，统一关闭用例类间并行。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
