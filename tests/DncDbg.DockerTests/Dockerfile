FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
WORKDIR /workspace/app
COPY adapter/ /opt/dncdbg/
COPY --chmod=755 adapter/dncdbg-dap /opt/dncdbg/dncdbg-dap
COPY app/ /workspace/app/
COPY Program.cs /workspace/source/Program.cs
