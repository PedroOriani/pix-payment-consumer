﻿using System;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using PixConsumer.DTOs;

//var connString = "Host=localhost;Username=postgres;Password=2483;Database=pix"; //Local
var connString = "Host=172.22.0.7;Username=postgres;Password=postgres;Database=pixAPI_docker"; //Docker
await using var conn = new NpgsqlConnection(connString);
await conn.OpenAsync();

string queueName = "payments";
var factory = new ConnectionFactory
{
    HostName = "localhost",
    UserName = "guest",
    Password = "guest"
};
using var connection = factory.CreateConnection();
using var channel = connection.CreateModel();

channel.QueueDeclare(
    queue: queueName,
    durable: true,
    exclusive: false,
    autoDelete: false,
    arguments: null
);

Console.WriteLine("[*] Waiting for messages...");

var consumer = new EventingBasicConsumer(channel);
consumer.Received += async (model, ea) =>
{
    var body = ea.Body.ToArray();
    var message = Encoding.UTF8.GetString(body);

    PaymentDTO payment = JsonSerializer.Deserialize<PaymentDTO>(message);
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase // Define a política de nomenclatura para minúsculas
    };
    var jsonContent = JsonSerializer.Serialize(payment, jsonOptions);

    using var httpClient = new HttpClient();
    int timeToVerifyBug = 5;
    httpClient.Timeout = TimeSpan.FromMinutes(timeToVerifyBug);
    var startTime = DateTime.UtcNow; // Registra o momento atual
    var response = await httpClient.PostAsync("http://localhost:5039/payments/pix", new StringContent(jsonContent, Encoding.UTF8, "application/json"));
    var statusResponse = response.IsSuccessStatusCode ? "SUCCESS" : "FAILED";

    // Verifica se o status é SUCCESS e se já se passaram mais de 2 minutos desde o início do processamento
    if (statusResponse == "SUCCESS" && DateTime.UtcNow - startTime > TimeSpan.FromMinutes(2))
    {
        Console.WriteLine("Mais de 2 minutos se passaram. Alterando o status para FAILED.");

        await using (var cmd = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = (@status), \"UpdatedAt\" = @updatedAt WHERE \"Id\" = @id", conn))
        {
            cmd.Parameters.AddWithValue("id", "FAILED");
            cmd.Parameters.AddWithValue("status", statusResponse);
            cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }
        return;
    }

    await using (var cmd = new NpgsqlCommand("UPDATE \"Payments\" SET \"Status\" = (@status), \"UpdatedAt\" = @updatedAt WHERE \"Id\" = @id", conn))
    {
        cmd.Parameters.AddWithValue("status", statusResponse);
        cmd.Parameters.AddWithValue("id", payment.Id);
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    Console.WriteLine("Payment updated!");
};

channel.BasicConsume(
    queue: queueName,
    autoAck: true,
    consumer: consumer
);

Console.WriteLine("Press [enter] to exit");
Console.ReadLine();
