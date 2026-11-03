using UnityEngine;

public static class CoordinateConverter
{
    public static Vector3 ConvertKukaToUnity(float kukaX, float kukaY, float kukaZ)
    {
    
        float unityY = kukaZ;

       
    
        float unityZ = -kukaY; 

       
        float unityX = -kukaX;

        return new Vector3(unityX, unityY, unityZ);
    }
}