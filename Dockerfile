FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY OrderTrackerBot.sln .
COPY src/OrderTrackerBot.Api/OrderTrackerBot.Api.csproj src/OrderTrackerBot.Api/
COPY src/OrderTrackerBot.Application/OrderTrackerBot.Application.csproj src/OrderTrackerBot.Application/
COPY src/OrderTrackerBot.Infrastructure/OrderTrackerBot.Infrastructure.csproj src/OrderTrackerBot.Infrastructure/
COPY src/OrderTrackerBot.Domain/OrderTrackerBot.Domain.csproj src/OrderTrackerBot.Domain/
COPY tests/OrderTrackerBot.Tests/OrderTrackerBot.Tests.csproj tests/OrderTrackerBot.Tests/
RUN dotnet restore src/OrderTrackerBot.Api/OrderTrackerBot.Api.csproj

COPY . .
RUN dotnet publish src/OrderTrackerBot.Api/OrderTrackerBot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app .

ENTRYPOINT ["dotnet", "OrderTrackerBot.Api.dll"]
