FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
RUN rm -f /etc/apt/sources.list /etc/apt/sources.list.d/*.sources /etc/apt/sources.list.d/*.list \
    && printf 'Types: deb\nURIs: https://snapshot.ubuntu.com/ubuntu/20261028T000000Z/\nSuites: noble noble-updates noble-security\nComponents: main universe\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' > /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update -o APT::Update::Error-Mode=any \
    && apt-get install -y --no-install-recommends g++ python3 procps openssh-server \
    && dpkg-query -W > /opt/dncdbg-os-packages.txt \
    && rm -rf /var/lib/apt/lists/* /etc/ssh/ssh_host_* \
    && mkdir -p /run/sshd /root/.ssh /device-sessions
ARG DEVICE_UID=1001
RUN useradd --create-home --user-group --uid "$DEVICE_UID" --password '*' --shell /bin/sh debugger \
    && mkdir -p /home/debugger/.ssh && chmod 700 /home/debugger/.ssh \
    && chown debugger:debugger /home/debugger/.ssh
WORKDIR /workspace
COPY . .
RUN dotnet build tests/DncDbg.NativeHostTests/Payload/Payload.csproj -c Debug --nologo -v:q \
    && dotnet publish tests/RemoteBreakpointApp -c Debug -o build/application --nologo -v:q \
    && cp tests/DncDbg.NativeHostTests/Payload/Payload.runtimeconfig.json build/application/RemoteBreakpointApp.runtimeconfig.json \
    && cp build/application/*.dll build/application/*.pdb build/application/*.json tests/DncDbg.NativeHostTests/Payload/bin/Debug/net10.0/ \
    && arch="$(uname -m)" \
    && if [ "$arch" = aarch64 ]; then rid=linux-arm64; else rid=linux-x64; fi \
    && mkdir -p "tests/DncDbg.NativeHostTests/Payload/bin/Debug/net10.0/runtimes/$rid/native" \
    && c++ -shared -fPIC tests/DncDbg.NativeHostTests/Payload/closure.cpp \
       -o "tests/DncDbg.NativeHostTests/Payload/bin/Debug/net10.0/runtimes/$rid/native/libclosure_probe.so" \
    && python3 src/DncDbg.Remote.Target/publish.py --rid "$rid" --include-runtime \
      --payload-directory tests/DncDbg.NativeHostTests/Payload/bin/Debug/net10.0 --output build/device-package \
    && cp -a build/device-package/control /opt/control \
    && cp -a build/device-package/runtime /opt/runtime \
    && python3 src/DncDbg.Remote.Target/publish.py --rid "$rid" \
      --runtime-directory /opt/runtime/shared/Microsoft.NETCore.App/11.0.0-rc.1.26425.128 \
      --payload-directory tests/DncDbg.NativeHostTests/Payload/bin/Debug/net10.0 --output build/device-bundle \
    && cp -a build/device-bundle /opt/source-package
CMD ["sleep", "infinity"]
