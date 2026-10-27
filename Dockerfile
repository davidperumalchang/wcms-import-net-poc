FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/WcmsImport/WcmsImport.csproj src/WcmsImport/
RUN dotnet restore src/WcmsImport/WcmsImport.csproj
COPY src/WcmsImport/ src/WcmsImport/
RUN dotnet publish src/WcmsImport/WcmsImport.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# each role binds its own port (3000, 4000, 5000), so clear the image's default of 8080
ENV ASPNETCORE_HTTP_PORTS=
USER $APP_UID
# one image; each compose service passes its role (api, importer, notifier, mock-cms, mock-upstream) as the command
ENTRYPOINT ["dotnet", "WcmsImport.dll"]
CMD ["api"]
