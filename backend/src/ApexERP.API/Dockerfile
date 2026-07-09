FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/ApexERP.Domain/ApexERP.Domain.csproj", "ApexERP.Domain/"]
COPY ["src/ApexERP.Application/ApexERP.Application.csproj", "ApexERP.Application/"]
COPY ["src/ApexERP.Infrastructure/ApexERP.Infrastructure.csproj", "ApexERP.Infrastructure/"]
COPY ["src/ApexERP.API/ApexERP.API.csproj", "ApexERP.API/"]
RUN dotnet restore "ApexERP.API/ApexERP.API.csproj"
COPY src/ .
WORKDIR /src/ApexERP.API
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ApexERP.API.dll"]
