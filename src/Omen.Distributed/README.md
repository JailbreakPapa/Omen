Omen.Distributed - OmenNet

Quickstart:

- Start the Coordinator: `dotnet run --project src/Omen.CLI -- coordinator --port 5051 --dashboard`
- Start an Agent: `dotnet run --project src/Omen.CLI -- agent --coordinator localhost:5051 --jobs 4`

See `Protos/` for gRPC API definitions and `Server/` for implementation details.