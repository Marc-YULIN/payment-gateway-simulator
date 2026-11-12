FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props nuget.config ./
COPY src/ src/
RUN dotnet restore src/PaymentSim.Api/PaymentSim.Api.csproj
RUN dotnet publish src/PaymentSim.Api/PaymentSim.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /app/appdata && chown -R $APP_UID:$APP_UID /app/appdata
ENV ASPNETCORE_URLS=http://+:5102
EXPOSE 5102
USER $APP_UID
ENTRYPOINT ["dotnet", "PaymentSim.Api.dll"]
