FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30 AS runtime
FROM mcr.microsoft.com/powershell:7.5-ubuntu-24.04@sha256:042240d57ec9e47e511033b92625a8d95875ee5860af3015992c248b58a8be81 AS powershell
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
COPY --from=runtime /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14
COPY --from=powershell /opt/microsoft/powershell/7 /opt/microsoft/powershell/7
RUN ln -s /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && apt-get update -o APT::Update::Error-Mode=any \
    && apt-get install -y --no-install-recommends cmake clang llvm build-essential libcurl4-openssl-dev git \
    && dpkg-query -W > /opt/evaluation-packages.txt
ENV DOTNET_CLI_USE_MSBUILD_SERVER=false MSBUILDUSESERVER=0 DOTNET_NOLOGO=true
COPY historical /native/historical
COPY current /native/current
RUN CC=clang CXX=clang++ cmake -S /native/historical -B /native/historical-build \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/opt/native-historical \
    && cmake --build /native/historical-build --target install --parallel 2 \
    && CC=clang CXX=clang++ cmake -S /native/current -B /native/current-build \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/opt/native-current \
    && cmake --build /native/current-build --target install --parallel 2
COPY baseline /baseline
RUN dotnet publish /baseline/src/DncDbg.Dap -c Release -o /opt/managed-baseline --nologo -v:q
WORKDIR /workspace/source
