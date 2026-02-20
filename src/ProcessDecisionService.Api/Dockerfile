FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app
COPY . .
RUN dotnet restore
ENV ASPNETCORE_URLS=http://+:8888
CMD ["dotnet","run","--no-launch-profile"]
