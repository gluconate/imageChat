FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY ImageChat.csproj packages.lock.json ./
RUN dotnet restore ImageChat.csproj --locked-mode
COPY . .
RUN dotnet publish ImageChat.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/data && chown -R app:app /app/data
ENV ASPNETCORE_URLS=http://+:8080 \
    URLS=http://+:8080 \
    Storage__Directory=/app/data \
    AllowedHosts=localhost;127.0.0.1;[::1]
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ImageChat.dll"]