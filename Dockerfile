FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet publish kustodian.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY src/data/ /app/data/
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /app/environments
ENTRYPOINT ["dotnet", "kustodian.dll"]
