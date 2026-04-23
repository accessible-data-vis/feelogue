using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

public class IntArrayConverter : JsonConverter<int[]>
{
    public override int[] ReadJson(JsonReader reader, Type objectType, int[] existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        JArray array = JArray.Load(reader);
        int[] result = new int[array.Count];

        for (int i = 0; i < array.Count; i++)
        {
            result[i] = (int)array[i].ToObject<long>(); // Convert Int64 to Int32
        }

        return result;
    }

    public override void WriteJson(JsonWriter writer, int[] value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}

public class IntArray2DConverter : JsonConverter<int[][]>
{
    public override int[][] ReadJson(JsonReader reader, Type objectType, int[][] existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        JArray outerArray = JArray.Load(reader);
        int[][] result = new int[outerArray.Count][];

        for (int i = 0; i < outerArray.Count; i++)
        {
            JArray innerArray = (JArray)outerArray[i];
            result[i] = new int[innerArray.Count];

            for (int j = 0; j < innerArray.Count; j++)
            {
                result[i][j] = (int)innerArray[j].ToObject<long>(); // Convert Int64 to Int32
            }
        }

        return result;
    }

    public override void WriteJson(JsonWriter writer, int[][] value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}
