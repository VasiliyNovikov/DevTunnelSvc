ARG VERSION=0.0.0-dev
FROM registry.access.redhat.com/ubi8/ubi:8.10 AS build
RUN dnf install -y dotnet-sdk-11.0 && dnf clean all
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props code-metrics-baseline.props ./
COPY DevTunnelSvc/ DevTunnelSvc/
COPY eng/release.cs eng/Repository.cs eng/
ARG VERSION
# Do not leave a compiler server attached to the container's build step.
RUN --mount=type=secret,id=nuget-config,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet build eng/release.cs -c Release --disable-build-servers -o /tmp/release-tool </dev/null && \
    UseSharedCompilation=false dotnet /tmp/release-tool/release.dll "$VERSION" </dev/null

FROM registry.access.redhat.com/ubi8/ubi:8.10 AS verified
RUN dnf install -y libicu && dnf clean all
COPY --from=build /src/artifacts/packages/ /packages/
ARG VERSION
RUN --network=none set -eu; \
    test "$(getconf GNU_LIBC_VERSION)" = 'glibc 2.28'; \
    set -- /packages/linux-*/devtunnelsvc-linux-*; test "$#" -eq 1; \
    test "$("$1" --version)" = "$VERSION"; "$1" --help; \
    "$1" service run & pid=$!; trap 'kill "$pid" 2>/dev/null || true' EXIT; \
    "$1" status | grep -F 'Service running; signed out.'; \
    if "$1" create >/tmp/create.out 2>/tmp/create.err; then exit 1; fi; \
    grep -F 'Not signed in' /tmp/create.err; \
    "$1" service stop; wait "$pid"; trap - EXIT

FROM scratch
COPY --from=verified /packages/ /
