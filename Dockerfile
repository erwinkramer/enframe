FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0-resolute-aot AS build
ARG TARGETARCH=x64
ARG SERVICENAME=Enframe.Mcp

WORKDIR /source
COPY src/ src/

RUN dotnet restore src/$SERVICENAME/$SERVICENAME.csproj -r linux-$TARGETARCH
RUN dotnet publish src/$SERVICENAME/$SERVICENAME.csproj -c Release -r linux-$TARGETARCH --no-restore -o /app && \
    # Create a symlink to the service executable with a generic name, 
    # so that the runtime image can use a common entry point
    ln -s /app/$SERVICENAME /app/app-entrypoint

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled
EXPOSE 8080

WORKDIR /app
COPY --from=build /app/ /app/

ENTRYPOINT ["/app/app-entrypoint"]
