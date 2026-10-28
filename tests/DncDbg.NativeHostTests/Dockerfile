FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble
RUN apt-get update \
    && apt-get install -y --no-install-recommends g++ python3 procps \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /workspace
COPY . .
RUN python3 tests/DncDbg.NativeHostTests/install_runtime.py \
    && dotnet build tests/DncDbg.NativeHostTests -c Release --nologo -v:q \
    && python3 src/DncDbg.Remote.Target/build.py --test-host
ENTRYPOINT ["dotnet", "run", "--project", "tests/DncDbg.NativeHostTests", "-c", "Release", "--no-build", "--", "--output", "build/validation/native-host-docker"]
