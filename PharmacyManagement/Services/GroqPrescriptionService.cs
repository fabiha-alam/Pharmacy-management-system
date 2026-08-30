using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PharmacyManagement.Services
{
    public class GroqPrescriptionService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public GroqPrescriptionService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<string> ExtractMedicineInfoAsync(string imageFullPath)
        {
            try
            {
                byte[] imageBytes = await File.ReadAllBytesAsync(imageFullPath);
                string base64Image = Convert.ToBase64String(imageBytes);

                var client = _httpClientFactory.CreateClient("GroqClient");

                var requestBody = new
                {
                    model = "qwen/qwen3.6-27b",
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = "You are a medical parser. Your ONLY job is to look at the prescription image and output a simple, comma-separated list of the medicine names found (e.g., Ursodeoxycholic, Silymarin, Pantoprazole, Paracetamol). Do NOT include dosages, quantities, instructions, paragraphs, markdown, or reasoning. Just the medicine names."
                        },
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = "List only the medicine names as a comma-separated list." },
                                new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64Image}" } }
                            }
                        }
                    },
                    max_tokens = 512
                };

                string jsonPayload = JsonSerializer.Serialize(requestBody);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("chat/completions", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorDetails = await response.Content.ReadAsStringAsync();
                    return $"Groq API Error: {errorDetails}";
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonResponse);

                var textResult = doc.RootElement.GetProperty("choices")[0]
                                     .GetProperty("message")
                                     .GetProperty("content")
                                     .GetString();

                if (string.IsNullOrEmpty(textResult))
                {
                    return "No text returned from AI.";
                }

                // 1. Strip out any reasoning block if present
                if (textResult.Contains("</think>", StringComparison.OrdinalIgnoreCase))
                {
                    int lastIndex = textResult.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    textResult = textResult.Substring(lastIndex + "</think>".Length);
                }

                textResult = Regex.Replace(textResult, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);

                // extra conversational wrappers if the AI adds them
                textResult = textResult.Replace("```", "").Replace("json", "").Trim();

                return textResult;
            }
            catch (Exception ex)
            {
                return $"Processing Error: {ex.Message}";
            }
        }
    }
}