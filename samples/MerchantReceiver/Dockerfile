FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props nuget.config ./
COPY src/PaymentSim.Signing/ src/PaymentSim.Signing/
COPY samples/MerchantReceiver/ samples/MerchantReceiver/
RUN dotnet restore samples/MerchantReceiver/MerchantReceiver.csproj
RUN dotnet publish samples/MerchantReceiver/MerchantReceiver.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:5112
EXPOSE 5112
USER $APP_UID
ENTRYPOINT ["dotnet", "MerchantReceiver.dll"]
