## Scheduling

Experimenting with various scheduling strategies on top of the various thread pool implementations.

Use `dotnet counters` to measure the invocation throughput:

```
dotnet counters monitor -p {pid} --providers System.Runtime MyApp
```
