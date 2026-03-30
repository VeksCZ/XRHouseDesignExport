using UnityEngine;

// Tento skript slouzi jen k tomu, aby Unity nevyhodilo shadery z buildu
public class ShaderInclude : MonoBehaviour
{
    [SerializeField] private Shader[] forcedShaders;
    
    // Tyto shadery pak v inspektoru priradíme, nebo je najdeme automaticky
    void Start() { 
        foreach(var s in forcedShaders) if(s != null) Debug.Log("Shader included: " + s.name);
    }
}