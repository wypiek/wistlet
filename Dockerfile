# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Wistlet/Wistlet.csproj src/Wistlet/
RUN dotnet restore src/Wistlet/Wistlet.csproj
COPY src/ src/
RUN dotnet publish src/Wistlet/Wistlet.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:6969 \
    ASPNETCORE_ENVIRONMENT=Production \
    Wistlet__DataPath=/data
# The database, uploads and data-protection keys all live in /data.
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app .
USER app
VOLUME /data
EXPOSE 6969
ENTRYPOINT ["dotnet", "Wistlet.dll"]
